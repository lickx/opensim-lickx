/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Serialization;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Ionic.Zlib;
using GZipStream = Ionic.Zlib.GZipStream;
using CompressionMode = Ionic.Zlib.CompressionMode;
using CompressionLevel = Ionic.Zlib.CompressionLevel;
using OpenSim.Framework.Serialization.External;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// Prepare to write out an archive.
    /// </summary>
    public class ArchiveWriteRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The minimum major version of OAR that we can write.
        /// </summary>
        public static int MIN_MAJOR_VERSION = 0;

        /// <summary>
        /// The maximum major version of OAR that we can write.
        /// </summary>
        public static int MAX_MAJOR_VERSION = 1;

        /// <summary>
        /// Whether we're saving a multi-region archive.
        /// </summary>
        public bool MultiRegionFormat { get; set; }

        /// <summary>
        /// Determine whether this archive will save assets.  Default is true.
        /// </summary>
        public bool SaveAssets { get; set; }

        /// <summary>
        /// Determines which objects will be included in the archive, according to their permissions.
        /// Default is null, meaning no permission checks.
        /// </summary>
        public string FilterContent { get; set; }

        protected Scene m_rootScene;
        protected Stream m_saveStream;
        protected TarArchiveWriter m_archiveWriter;
        protected Guid m_requestId;
        protected Dictionary<string, object> m_options;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="module">Calling module</param>
        /// <param name="savePath">The path to which to save data.</param>
        /// <param name="requestId">The id associated with this request</param>
        /// <exception cref="System.IO.IOException">
        /// If there was a problem opening a stream for the file specified by the savePath
        /// </exception>
        public ArchiveWriteRequest(Scene scene, string savePath, Guid requestId) : this(scene, requestId)
        {
            try
            {
                m_saveStream = new GZipStream(new FileStream(savePath, FileMode.Create), CompressionMode.Compress, CompressionLevel.BestCompression);
            }
            catch (EntryPointNotFoundException e)
            {
                m_log.ErrorFormat(
                    "[ARCHIVER]: Mismatch between Mono and zlib1g library version when trying to create compression stream."
                        + "If you've manually installed Mono, have you appropriately updated zlib1g as well?");
                m_log.ErrorFormat("{0} {1}", e.Message, e.StackTrace);
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="scene">The root scene to archive</param>
        /// <param name="saveStream">The stream to which to save data.</param>
        /// <param name="requestId">The id associated with this request</param>
        public ArchiveWriteRequest(Scene scene, Stream saveStream, Guid requestId) : this(scene, requestId)
        {
            m_saveStream = saveStream;
        }

        protected ArchiveWriteRequest(Scene scene, Guid requestId)
        {
            m_rootScene = scene;
            m_requestId = requestId;
            m_archiveWriter = null;

            MultiRegionFormat = false;
            SaveAssets = true;
            FilterContent = null;
        }

        /// <summary>
        /// Archive the region requested.
        /// </summary>
        /// <exception cref="System.IO.IOException">if there was an io problem with creating the file</exception>
        public void ArchiveRegion(Dictionary<string, object> options)
        {
            m_options = options;

            if (options.ContainsKey("all") && (bool)options["all"])
                MultiRegionFormat = true;


            if (options.ContainsKey("noassets") && (bool)options["noassets"])
                SaveAssets = false;


            if (options.TryGetValue("checkPermissions", out Object temp))
                FilterContent = (string)temp;

            // Find the regions to archive
            ArchiveScenesGroup scenesGroup = new ArchiveScenesGroup();
            if (MultiRegionFormat)
            {
                m_log.InfoFormat("[ARCHIVER]: Saving {0} regions", SceneManager.Instance.Scenes.Count);
                SceneManager.Instance.ForEachScene(delegate(Scene scene)
                {
                    scenesGroup.AddScene(scene);
                });
            }
            else
            {
                scenesGroup.AddScene(m_rootScene);
            }
            scenesGroup.CalcSceneLocations();

            m_archiveWriter = new TarArchiveWriter(m_saveStream);

            try
            {
                // Write out control file. It should be first so that it will be found ASAP when loading the file.
                m_archiveWriter.WriteFile(ArchiveConstants.CONTROL_FILE_PATH, CreateControlFile(scenesGroup));
                m_log.InfoFormat("[ARCHIVER]: Added control file to archive.");

                // Archive the regions

                Dictionary<UUID, sbyte> assetUuids = new Dictionary<UUID, sbyte>();
                HashSet<UUID> failedIDs = new HashSet<UUID>();
                HashSet<UUID> uncertainAssetsUUIDs = new HashSet<UUID>();

                scenesGroup.ForEachScene(delegate(Scene scene)
                {
                    string regionDir = MultiRegionFormat ? scenesGroup.GetRegionDir(scene.RegionInfo.RegionID) : "";

                    UUID userId = scene.RegionInfo.EstateSettings.EstateOwner;
                    if (options.TryGetValue("tenant", out Object temp))
                    {
                        if (!UUID.TryParse((string)temp, out userId) || userId.IsZero())
                            userId = scene.RegionInfo.EstateSettings.EstateOwner;
                    }

                    ArchiveOneRegion(scene, regionDir, assetUuids, failedIDs, uncertainAssetsUUIDs, userId);
                });

                // Archive the assets

                if (SaveAssets)
                {
                    m_log.DebugFormat("[ARCHIVER]: Saving {0} assets", assetUuids.Count);
                    
                    AssetsRequest ar = new AssetsRequest(
                            new AssetsArchiver(m_archiveWriter), assetUuids,
                            failedIDs.Count,
                            m_rootScene.AssetService, m_rootScene.UserAccountService,
                            m_rootScene.RegionInfo.ScopeID, options, null);
                    ar.Execute();
                    assetUuids = null;
                }
                else
                {
                    m_log.DebugFormat("[ARCHIVER]: Not saving assets since --noassets was specified");
//                    CloseArchive(string.Empty);
                }
                CloseArchive(string.Empty);
            }
            catch (Exception e)
            {
                CloseArchive(e.Message);
                throw;
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
        }

        private void ArchiveOneRegion(Scene scene, string regionDir, Dictionary<UUID, sbyte> assetUuids,
            HashSet<UUID> failedIDs, HashSet<UUID>  uncertainAssetsUUIDs, UUID userId)
        {
            m_log.InfoFormat("[ARCHIVER]: Writing region {0}", scene.Name);

            EntityBase[] entities = scene.GetEntities();
            List<SceneObjectGroup> sceneObjects = new List<SceneObjectGroup>();

            int numObjectsSkippedPermissions = 0;

            // Filter entities so that we only have scene objects.
            // FIXME: Would be nicer to have this as a proper list in SceneGraph, since lots of methods
            // end up having to do this
            IPermissionsModule permissionsModule = scene.RequestModuleInterface<IPermissionsModule>();
            foreach (EntityBase entity in entities)
            {
                if (entity is SceneObjectGroup)
                {
                    SceneObjectGroup sceneObject = entity as SceneObjectGroup;

                    if (!sceneObject.IsDeleted && !sceneObject.IsAttachment && !sceneObject.IsTemporary && !sceneObject.inTransit)
                    {
                        if (userId != scene.RegionInfo.EstateSettings.EstateOwner && sceneObject.OwnerID != userId)
                        {
                            // A tenant= option was specified, skip object if not owned by tenant with userid
                            ++numObjectsSkippedPermissions;
                        }
                        else if (!CanUserArchiveObject(userId, sceneObject, FilterContent, permissionsModule))
                        {
                            // The user isn't allowed to copy/transfer this object, so it will not be included in the OAR.
                            ++numObjectsSkippedPermissions;
                        }
                        else
                        {
                            sceneObjects.Add(sceneObject);
                        }
                    }
                }
            }

            UuidGatherer assetGatherer = new UuidGatherer(scene.AssetService, assetUuids, failedIDs, uncertainAssetsUUIDs);
            int prevAssets = assetUuids.Count;

            if (SaveAssets)
            {
                foreach (SceneObjectGroup sceneObject in sceneObjects)
                {
                    int curErrorCntr = assetGatherer.ErrorCount;
                    int possible = assetGatherer.possibleNotAssetCount;
                    assetGatherer.AddForInspection(sceneObject);
                    assetGatherer.GatherAll();
                    curErrorCntr =  assetGatherer.ErrorCount - curErrorCntr;
                    possible = assetGatherer.possibleNotAssetCount - possible;
                    if(curErrorCntr > 0)
                    {
                        m_log.ErrorFormat("[ARCHIVER]: object {0} '{1}', at {2}, contains {3} references to missing or damaged assets",
                            sceneObject.UUID, sceneObject.Name ,sceneObject.AbsolutePosition.ToString(), curErrorCntr);
                        if(possible > 0)
                            m_log.WarnFormat("[ARCHIVER Warning]: object also contains {0} references that may not be assets or are missing", possible);
                    }
                    else if(possible > 0)
                    {
                        m_log.WarnFormat("[ARCHIVER Warning]: object {0} '{1}', at {2}, contains {3} references that may not be assets or are missing",
                            sceneObject.UUID, sceneObject.Name ,sceneObject.AbsolutePosition.ToString(), possible);
                    }
                }

                assetGatherer.GatherAll();

                GC.Collect();

                int errors = assetGatherer.FailedUUIDs.Count;
                m_log.DebugFormat(
                    "[ARCHIVER]: {0} region scene objects to save reference {1} possible assets",
                    sceneObjects.Count, assetUuids.Count - prevAssets + errors);
                if(errors > 0)
                    m_log.DebugFormat("[ARCHIVER]: {0} of these have problems or are not assets and will be ignored", errors);
            }

            if (numObjectsSkippedPermissions > 0)
            {
                m_log.DebugFormat(
                    "[ARCHIVER]: {0} scene objects skipped due to lack of permissions",
                    numObjectsSkippedPermissions);
            }

            // Make sure that we also request terrain texture assets
            RegionSettings regionSettings = scene.RegionInfo.RegionSettings;

            if (regionSettings.TerrainTexture1 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_1)
                assetUuids[regionSettings.TerrainTexture1] = (sbyte)AssetType.Texture;

            if (regionSettings.TerrainTexture2 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_2)
                assetUuids[regionSettings.TerrainTexture2] = (sbyte)AssetType.Texture;

            if (regionSettings.TerrainTexture3 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_3)
                assetUuids[regionSettings.TerrainTexture3] = (sbyte)AssetType.Texture;

            if (regionSettings.TerrainTexture4 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_4)
                assetUuids[regionSettings.TerrainTexture4] = (sbyte)AssetType.Texture;

            if (regionSettings.TerrainPBR1 != RegionSettings.DEFAULT_TERRAIN_PBR_1)
                assetGatherer.AddForInspection(regionSettings.TerrainPBR1);

            if (regionSettings.TerrainPBR2 != RegionSettings.DEFAULT_TERRAIN_PBR_2)
                assetGatherer.AddForInspection(regionSettings.TerrainPBR2);

            if (regionSettings.TerrainPBR3 != RegionSettings.DEFAULT_TERRAIN_PBR_3)
                assetGatherer.AddForInspection(regionSettings.TerrainPBR3);

            if (regionSettings.TerrainPBR4 != RegionSettings.DEFAULT_TERRAIN_PBR_4)
                assetGatherer.AddForInspection(regionSettings.TerrainPBR4);

            assetGatherer.GatherAll();

            if (scene.RegionEnvironment != null)
                scene.RegionEnvironment.GatherAssets(assetUuids);

            List<ILandObject> landObjects = scene.LandChannel.AllParcels();
            foreach (ILandObject lo in landObjects)
            {
                if(lo.LandData != null && lo.LandData.Environment != null)
                    lo.LandData.Environment.GatherAssets(assetUuids);
                if(lo.LandData.MediaID.IsNotZero())
                    assetUuids[lo.LandData.MediaID] = (sbyte)AssetType.Texture;
            }

            Save(scene, sceneObjects, regionDir);
            GC.Collect();
        }

        /// <summary>
        /// Checks whether the user has permission to export an object group to an OAR.
        /// </summary>
        /// <param name="user">The user</param>
        /// <param name="objGroup">The object group</param>
        /// <param name="filterContent">Which permissions to check: "C" = Copy, "T" = Transfer</param>
        /// <param name="permissionsModule">The scene's permissions module</param>
        /// <returns>Whether the user is allowed to export the object to an OAR</returns>
        private bool CanUserArchiveObject(UUID user, SceneObjectGroup objGroup, string filterContent, IPermissionsModule permissionsModule)
        {
            if (filterContent == null)
                return true;

            if (permissionsModule == null)
                return true;    // this shouldn't happen

            // Check whether the user is permitted to export all of the parts in the SOG. If any
            // part can't be exported then the entire SOG can't be exported.

            bool permitted = true;
            //int primNumber = 1;

            foreach (SceneObjectPart obj in objGroup.Parts)
            {
                uint perm;
                PermissionClass permissionClass = permissionsModule.GetPermissionClass(user, obj);
                switch (permissionClass)
                {
                    case PermissionClass.Owner:
                        perm = obj.BaseMask;
                        break;
                    case PermissionClass.Group:
                        perm = obj.GroupMask | obj.EveryoneMask;
                        break;
                    case PermissionClass.Everyone:
                    default:
                        perm = obj.EveryoneMask;
                        break;
                }

                bool canCopy = (perm & (uint)PermissionMask.Copy) != 0;
                bool canTransfer = (perm & (uint)PermissionMask.Transfer) != 0;

                // Special case: if Everyone can copy the object then this implies it can also be
                // Transferred.
                // However, if the user is the Owner then we don't check EveryoneMask, because it seems that the mask
                // always (incorrectly) includes the Copy bit set in this case. But that's a mistake: the viewer
                // does NOT show that the object has Everyone-Copy permissions, and doesn't allow it to be copied.
                if (permissionClass != PermissionClass.Owner)
                    canTransfer |= (obj.EveryoneMask & (uint)PermissionMask.Copy) != 0;

                bool partPermitted = true;
                if (filterContent.Contains("C") && !canCopy)
                    partPermitted = false;
                if (filterContent.Contains("T") && !canTransfer)
                    partPermitted = false;

                // If the user is the Creator of the object then it can always be included in the OAR
                bool creator = (obj.CreatorID.Guid == user.Guid);
                if (creator)
                    partPermitted = true;

                //string name = (objGroup.PrimCount == 1) ? objGroup.Name : string.Format("{0} ({1}/{2})", obj.Name, primNumber, objGroup.PrimCount);
                //m_log.DebugFormat("[ARCHIVER]: Object permissions: {0}: Base={1:X4}, Owner={2:X4}, Everyone={3:X4}, permissionClass={4}, checkPermissions={5}, canCopy={6}, canTransfer={7}, creator={8}, permitted={9}",
                //    name, obj.BaseMask, obj.OwnerMask, obj.EveryoneMask,
                //    permissionClass, checkPermissions, canCopy, canTransfer, creator, partPermitted);

                if (!partPermitted)
                {
                    permitted = false;
                    break;
                }

                //++primNumber;
            }

            return permitted;
        }

        /// <summary>
        /// Create the control file.
        /// </summary>
        /// <returns></returns>
        public string CreateControlFile(ArchiveScenesGroup scenesGroup)
        {
            int majorVersion;
            int minorVersion;

            if (MultiRegionFormat)
            {
                majorVersion = MAX_MAJOR_VERSION;
                minorVersion = 0;
            }
            else
            {
                // To support older versions of OpenSim, we continue to create single-region OARs
                // using the old file format. In the future this format will be discontinued.
                majorVersion = 0;
                minorVersion = 8;
            }
//
//            if (m_options.ContainsKey("version"))
//            {
//                string[] parts = m_options["version"].ToString().Split('.');
//                if (parts.Length >= 1)
//                {
//                    majorVersion = Int32.Parse(parts[0]);
//
//                    if (parts.Length >= 2)
//                        minorVersion = Int32.Parse(parts[1]);
//                }
//            }
//
//            if (majorVersion < MIN_MAJOR_VERSION || majorVersion > MAX_MAJOR_VERSION)
//            {
//                throw new Exception(
//                    string.Format(
//                        "OAR version number for save must be between {0} and {1}",
//                        MIN_MAJOR_VERSION, MAX_MAJOR_VERSION));
//            }
//            else if (majorVersion == MAX_MAJOR_VERSION)
//            {
//                // Force 1.0
//                minorVersion = 0;
//            }
//            else if (majorVersion == MIN_MAJOR_VERSION)
//            {
//                // Force 0.4
//                minorVersion = 4;
//            }

            m_log.InfoFormat("[ARCHIVER]: Creating version {0}.{1} OAR", majorVersion, minorVersion);
            if (majorVersion == 1)
            {
                m_log.WarnFormat("[ARCHIVER]: Please be aware that version 1.0 OARs are not compatible with OpenSim versions prior to 0.7.4. Do not use the --all option if you want to produce a compatible OAR");
            }

            String s;

            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter xtw = new XmlTextWriter(sw))
                {
                    xtw.Formatting = Formatting.Indented;
                    xtw.WriteStartDocument();
                    xtw.WriteStartElement("archive");
                    xtw.WriteAttributeString("major_version", majorVersion.ToString());
                    xtw.WriteAttributeString("minor_version", minorVersion.ToString());

                    xtw.WriteStartElement("creation_info");
                    DateTime now = DateTime.UtcNow;
                    TimeSpan t = now - new DateTime(1970, 1, 1);
                    xtw.WriteElementString("datetime", ((int)t.TotalSeconds).ToString());
                    if (!MultiRegionFormat)
                        xtw.WriteElementString("id", m_rootScene.RegionInfo.RegionID.ToString());
                    xtw.WriteEndElement();

                    xtw.WriteElementString("assets_included", SaveAssets.ToString());

                    if (MultiRegionFormat)
                    {
                        WriteRegionsManifest(scenesGroup, xtw);
                    }
                    else
                    {
                        xtw.WriteStartElement("region_info");
                        WriteRegionInfo(m_rootScene, xtw);
                        xtw.WriteEndElement();
                    }

                    xtw.WriteEndElement();

                    xtw.Flush();
                }

                s = sw.ToString();
            }

            return s;
        }

        /// <summary>
        /// Writes the list of regions included in a multi-region OAR.
        /// </summary>
        private static void WriteRegionsManifest(ArchiveScenesGroup scenesGroup, XmlTextWriter xtw)
        {
            xtw.WriteStartElement("regions");

            // Write the regions in order: rows from South to North, then regions from West to East.
            // The list of regions can have "holes"; we write empty elements in their position.

            for (uint y = (uint)scenesGroup.Rect.Top; y < scenesGroup.Rect.Bottom; ++y)
            {
                if (scenesGroup.Regions.TryGetValue(y, out SortedDictionary<uint, Scene> row))
                {
                    xtw.WriteStartElement("row");

                    for (uint x = (uint)scenesGroup.Rect.Left; x < scenesGroup.Rect.Right; ++x)
                    {
                        if (row.TryGetValue(x, out Scene scene))
                        {
                            xtw.WriteStartElement("region");
                            xtw.WriteElementString("id", scene.RegionInfo.RegionID.ToString());
                            xtw.WriteElementString("dir", scenesGroup.GetRegionDir(scene.RegionInfo.RegionID));
                            WriteRegionInfo(scene, xtw);
                            xtw.WriteEndElement();
                        }
                        else
                        {
                            // Write a placeholder for a missing region
                            xtw.WriteElementString("region", "");
                        }
                    }

                    xtw.WriteEndElement();
                }
                else
                {
                    // Write a placeholder for a missing row
                    xtw.WriteElementString("row", "");
                }
            }

            xtw.WriteEndElement();  // "regions"
        }

        protected static void WriteRegionInfo(Scene scene, XmlTextWriter xtw)
        {
            Vector2 size;

            size = new Vector2((float)scene.RegionInfo.RegionSizeX, (float)scene.RegionInfo.RegionSizeY);

            xtw.WriteElementString("size_in_meters", string.Format("{0},{1}", size.X, size.Y));
        }

        protected void Save(Scene scene, List<SceneObjectGroup> sceneObjects, string regionDir)
        {
            if (regionDir != string.Empty)
                regionDir = ArchiveConstants.REGIONS_PATH + regionDir + "/";

            m_log.InfoFormat("[ARCHIVER]: Adding region settings to archive.");

            // Write out region settings
            string settingsPath = String.Format("{0}{1}{2}.xml",
                regionDir, ArchiveConstants.SETTINGS_PATH, scene.RegionInfo.RegionName);
            m_archiveWriter.WriteFile(settingsPath, RegionSettingsSerializer.Serialize(scene.RegionInfo.RegionSettings, scene.RegionEnvironment, scene.RegionInfo.EstateSettings));

            m_log.InfoFormat("[ARCHIVER]: Adding parcel settings to archive.");

            // Write out land data (aka parcel) settings
            List<ILandObject> landObjects = scene.LandChannel.AllParcels();
            foreach (ILandObject lo in landObjects)
            {
                LandData landData = lo.LandData;
                string landDataPath
                    = String.Format("{0}{1}", regionDir, ArchiveConstants.CreateOarLandDataPath(landData));
                m_archiveWriter.WriteFile(landDataPath, LandDataSerializer.Serialize(landData, m_options));
            }

            m_log.InfoFormat("[ARCHIVER]: Adding terrain information to archive.");

            // Write out terrain
            string terrainPath = String.Format("{0}{1}{2}.r32",
                regionDir, ArchiveConstants.TERRAINS_PATH, scene.RegionInfo.RegionName);

            using (MemoryStream ms = new MemoryStream())
            {
                scene.RequestModuleInterface<ITerrainModule>().SaveToStream(terrainPath, ms);
                m_archiveWriter.WriteFile(terrainPath, ms.ToArray());
            }

            m_log.InfoFormat("[ARCHIVER]: Adding scene objects to archive.");

            // Write out scene object metadata
            IRegionSerialiserModule serializer = scene.RequestModuleInterface<IRegionSerialiserModule>();
            foreach (SceneObjectGroup sceneObject in sceneObjects)
            {
                //m_log.DebugFormat("[ARCHIVER]: Saving {0} {1}, {2}", entity.Name, entity.UUID, entity.GetType());
                if(sceneObject.IsDeleted || sceneObject.inTransit)
                    continue;
                string serializedObject = serializer.SerializeGroupToXml2(sceneObject, m_options);
                string objectPath = string.Format("{0}{1}", regionDir, ArchiveHelpers.CreateObjectPath(sceneObject));
                m_archiveWriter.WriteFile(objectPath, serializedObject);
            }
        }

        protected void ReceivedAllAssets(ICollection<UUID> assetsFoundUuids, ICollection<UUID> assetsNotFoundUuids, bool timedOut)
        {
            string errorMessage;

            if (timedOut)
            {
                errorMessage = "Loading assets timed out";
            }
            else
            {
                foreach (UUID uuid in assetsNotFoundUuids)
                {
                    m_log.DebugFormat("[ARCHIVER]: Could not find asset {0}", uuid);
                }

                //            m_log.InfoFormat(
                //                "[ARCHIVER]: Received {0} of {1} assets requested",
                //                assetsFoundUuids.Count, assetsFoundUuids.Count + assetsNotFoundUuids.Count);

                errorMessage = String.Empty;
            }

            CloseArchive(errorMessage);
        }

        /// <summary>
        /// Closes the archive and notifies that we're done.
        /// </summary>
        /// <param name="errorMessage">The error that occurred, or empty for success</param>
        protected void CloseArchive(string errorMessage)
        {
            try
            {
                if (m_archiveWriter != null)
                    m_archiveWriter.Close();
                m_saveStream.Close();
            }
            catch (Exception e)
            {
                m_log.Error(string.Format("[ARCHIVER]: Error closing archive: {0} ", e.Message), e);
                if (errorMessage.Length == 0)
                    errorMessage = e.Message;
            }

            m_log.InfoFormat("[ARCHIVER]: Finished writing out OAR for {0}", m_rootScene.RegionInfo.RegionName);

            m_rootScene.EventManager.TriggerOarFileSaved(m_requestId, errorMessage);
        }
    }
}
