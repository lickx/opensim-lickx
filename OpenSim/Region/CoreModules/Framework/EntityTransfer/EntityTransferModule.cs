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
using System.Net;
using System.Reflection;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EntityTransferModule")]
    public class EntityTransferModule : INonSharedRegionModule, IEntityTransferModule, IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ENTITY TRANSFER MODULE]";
        private static readonly string OutfitTPError = "destination region does not support the Outfit you are wearing. Please retry with a simpler one";

        public EntityTransferModule()
        {
        }

        ~EntityTransferModule()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        bool disposed;
        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                m_bannedRegionCache?.Dispose();
                m_bannedRegionCache = null;
            }
        }

        /// <summary>
        /// If true then on a teleport, the source region waits for a callback from the destination region.  If
        /// a callback fails to arrive within a set time then the user is pulled back into the source region.
        /// </summary>
        public bool WaitForAgentArrivedAtDestination { get; set; } = true;

        /// <summary>
        /// If true then we ask the viewer to disable teleport cancellation and ignore teleport requests.
        /// </summary>
        /// <remarks>
        /// This is useful in situations where teleport is very likely to always succeed and we want to avoid a
        /// situation where avatars can be come 'stuck' due to a failed teleport cancellation.  Unfortunately, the
        /// nature of the teleport protocol makes it extremely difficult (maybe impossible) to make teleport
        /// cancellation consistently suceed.
        /// </remarks>
        public bool DisableInterRegionTeleportCancellation { get; set; }

        /// <summary>
        /// Number of times inter-region teleport was attempted.
        /// </summary>
        private Stat m_interRegionTeleportAttempts;

        /// <summary>
        /// Number of times inter-region teleport was aborted (due to simultaneous client logout).
        /// </summary>
        private Stat m_interRegionTeleportAborts;

        /// <summary>
        /// Number of times inter-region teleport was successfully cancelled by the client.
        /// </summary>
        private Stat m_interRegionTeleportCancels;

        /// <summary>
        /// Number of times inter-region teleport failed due to server/client/network problems (e.g. viewer failed to
        /// connect with destination region).
        /// </summary>
        /// <remarks>
        /// This is not necessarily a problem for this simulator - in open-grid/hg conditions, viewer connectivity to
        /// destination simulator is unknown.
        /// </remarks>
        private Stat m_interRegionTeleportFailures;

        protected GridInfo m_thisGridInfo;

        protected bool m_Enabled = false;

        protected Scene m_scene;
        public Scene Scene
        {
            get
            {
                return m_scene;
            }
         }

        protected string m_sceneName;
        protected RegionInfo m_sceneRegionInfo;
        protected ulong m_sceneRegionHandler;
        /// <summary>
        /// Handles recording and manipulation of state for entities that are in transfer within or between regions
        /// (cross or teleport).
        /// </summary>
        private EntityTransferStateMachine m_entityTransferStateMachine;

        // For performance, we keed a cached of banned regions so we don't keep going
        //    to the grid service.
        private class BannedRegionCache
        {
            private ExpiringCacheOS<ulong, Dictionary<UUID, double>> m_bannedRegions = new(15000);

            public BannedRegionCache()
            {
            }

            ~BannedRegionCache()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (m_bannedRegions != null)
                {
                    m_bannedRegions.Dispose();
                    m_bannedRegions = null;
                }
            }

            // Return 'true' if there is a valid ban entry for this agent in this region
            public bool IfBanned(ulong pRegionHandle, UUID pAgentID)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock(idCache)
                    {
                        if (idCache.TryGetValue(pAgentID, out double exp))
                        {
                            if(exp < Util.GetTimeStamp())
                                return true;
                            else
                                idCache.Remove(pAgentID);
                        }
                    }
                }
                return false;
            }

            public void Add(ulong pRegionHandle, UUID pAgentID, double newTime)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock (idCache)
                    {
                        idCache[pAgentID] = Util.GetTimeStamp() + newTime;
                        m_bannedRegions.AddOrUpdate(pRegionHandle, idCache, newTime);
                    }
                }
                else
                {
                    idCache = new Dictionary<UUID, double>
                    {
                        [pAgentID] = Util.GetTimeStamp() + newTime
                    };
                    m_bannedRegions.AddOrUpdate(pRegionHandle, idCache, newTime);
                }
            }

            // Remove the agent from the region's banned list
            public void Remove(ulong pRegionHandle, UUID pAgentID)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock (idCache)
                        idCache.Remove(pAgentID);
                }
            }
        }

        private BannedRegionCache m_bannedRegionCache = new();

        private IEventQueue m_eqModule;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public virtual string Name
        {
            get { return "BasicEntityTransferModule"; }
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    InitialiseCommon(source);
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: {0} enabled.", Name);
                }
            }
        }

        /// <summary>
        /// Initialize config common for this module and any descendents.
        /// </summary>
        /// <param name="source"></param>
        protected virtual void InitialiseCommon(IConfigSource source)
        {
            IConfig transferConfig = source.Configs["EntityTransfer"];
            if (transferConfig != null)
            {
                DisableInterRegionTeleportCancellation
                    = transferConfig.GetBoolean("DisableInterRegionTeleportCancellation", false);

                WaitForAgentArrivedAtDestination
                    = transferConfig.GetBoolean("wait_for_callback", WaitForAgentArrivedAtDestination);
            }

            m_entityTransferStateMachine = new EntityTransferStateMachine(this);

            m_Enabled = true;
        }

        public virtual void PostInitialise()
        {
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;
            m_sceneName = scene.Name;
            m_sceneRegionInfo = scene.RegionInfo;
            m_sceneRegionHandler = m_sceneRegionInfo.RegionHandle;
            m_thisGridInfo = scene.SceneGridInfo;

            m_interRegionTeleportAttempts =
                new Stat(
                    "InterRegionTeleportAttempts",
                    "Number of inter-region teleports attempted.",
                    "This does not count attempts which failed due to pre-conditions (e.g. target simulator refused access).\n"
                        + "You can get successfully teleports by subtracting aborts, cancels and teleport failures from this figure.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportAborts =
                new Stat(
                    "InterRegionTeleportAborts",
                    "Number of inter-region teleports aborted due to client actions.",
                    "The chief action is simultaneous logout whilst teleporting.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportCancels =
                new Stat(
                    "InterRegionTeleportCancels",
                    "Number of inter-region teleports cancelled by the client.",
                    null,
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportFailures =
                new Stat(
                    "InterRegionTeleportFailures",
                    "Number of inter-region teleports that failed due to server/client/network issues.",
                    "This number may not be very helpful in open-grid/hg situations as the network connectivity/quality of destinations is uncontrollable.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            StatsManager.RegisterStat(m_interRegionTeleportAttempts);
            StatsManager.RegisterStat(m_interRegionTeleportAborts);
            StatsManager.RegisterStat(m_interRegionTeleportCancels);
            StatsManager.RegisterStat(m_interRegionTeleportFailures);

            scene.RegisterModuleInterface<IEntityTransferModule>(this);
            scene.EventManager.OnNewClient += OnNewClient;
        }

        protected virtual void OnNewClient(IClientAPI client)
        {
            client.OnTeleportHomeRequest += TriggerTeleportHome;
            client.OnTeleportLandmarkRequest += RequestTeleportLandmark;

            if (!DisableInterRegionTeleportCancellation)
                client.OnTeleportCancel += OnClientCancelTeleport;

            client.OnConnectionClosed += OnConnectionClosed;
        }

        public virtual void Close()
        {
            Dispose();
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (m_Enabled)
            {
                StatsManager.DeregisterStat(m_interRegionTeleportAttempts);
                StatsManager.DeregisterStat(m_interRegionTeleportAborts);
                StatsManager.DeregisterStat(m_interRegionTeleportCancels);
                StatsManager.DeregisterStat(m_interRegionTeleportFailures);
                scene.EventManager.OnNewClient -= OnNewClient;
                m_thisGridInfo = null;
            }
        }

        public virtual void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_eqModule = m_scene.RequestModuleInterface<IEventQueue>();
        }

        #endregion

        #region Agent Teleports

        public virtual void OnConnectionClosed(IClientAPI client)
        {
            if (client.IsLoggingOut && m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Aborting))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport request from {0} in {1} due to simultaneous logout",
                    client.Name, m_sceneName);
            }
        }

        private void OnClientCancelTeleport(IClientAPI client)
        {
            m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Cancelling);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Received teleport cancel request from {0} in {1}", client.Name, m_sceneName);
        }

        // Attempt to teleport the ScenePresence to the specified position in the specified region (spec'ed by its handle).
        public void Teleport(ScenePresence sp, ulong regionHandle, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            UUID spUUID = sp.UUID;
            if (m_scene.Permissions.IsGridGod(spUUID))
            {
                // This user will be a God in the destination scene, too
                teleportFlags |= (uint)TeleportFlags.Godlike;
            }

            else if (!m_scene.Permissions.CanTeleport(spUUID))
                return;

            string destinationRegionName = "(not found)";

            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(spUUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2}@{3} - agent is already in transit.",
                    sp.Name, spUUID, position, regionHandle);

                sp.ControllingClient.SendTeleportFailed("Previous teleport process incomplete.  Please retry shortly.");

                return;
            }

            try
            {
                if (Util.CompareRegionHandles(regionHandle, position, m_sceneRegionInfo, out Vector3 roffset))
                {
                    if(!sp.AllowMovement)
                    {
                        sp.ControllingClient.SendTeleportFailed("You are frozen");
                        m_entityTransferStateMachine.ResetFromTransit(spUUID);
                        return;
                    }

                    destinationRegionName = m_sceneName;
                    TeleportAgentWithinRegion(sp, roffset, lookAt, teleportFlags);
                }
                else // Another region possibly in another simulator
                {
                    GridRegion finalDestination = null;
                    try
                    {
                        TeleportAgentToDifferentRegion(sp, regionHandle, position, lookAt, teleportFlags, out finalDestination);
                    }
                    finally
                    {
                        if (finalDestination != null)
                            destinationRegionName = finalDestination.RegionName;
                    }
                }
            }
            catch (Exception e)
            {
                
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, m_sceneName, position, destinationRegionName,
                    e.Message, e.StackTrace);
                if(sp != null && sp.ControllingClient != null && !sp.IsDeleted)
                    sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(spUUID);
            }
        }

        /// <summary>
        /// Teleports the agent within its current region.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        private void TeleportAgentWithinRegion(ScenePresence sp, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleport for {0} to {1} within {2}",
                sp.Name, position, m_sceneName);

            // Teleport within the same region
            if (!m_scene.PositionIsInCurrentRegion(position) || position.Z < 0)
            {
                Vector3 emergencyPos = new(128, 128, 128);

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: RequestTeleportToLocation() was given an illegal position of {0} for avatar {1}, {2} in {3}.  Substituting {4}",
                    position, sp.Name, sp.UUID, m_sceneName, emergencyPos);

                position = emergencyPos;
            }

            // Check Default Location (Also See ScenePresence.CompleteMovement)
            if (position.X == 128f && position.Y == 128f && position.Z == 22.5f)
                position = m_sceneRegionInfo.DefaultLandingPoint;

            float localHalfAVHeight = sp.Appearance is null ? 0.8f : sp.Appearance.AvatarHeight / 2;
            float posZLimit = m_scene.GetGroundHeight(position.X, position.Y);
            posZLimit += localHalfAVHeight + 0.1f;

            if ((position.Z < posZLimit) && !(Single.IsInfinity(posZLimit) || Single.IsNaN(posZLimit)))
            {
                position.Z = posZLimit;
            }
/*
            if(!sp.CheckLocalTPLandingPoint(ref position))
            {
                sp.ControllingClient.SendTeleportFailed("Not allowed at destination");
                return;
            }
*/
            if (sp.Flying)
                teleportFlags |= (uint)TeleportFlags.IsFlying;

            UUID spUUID = sp.UUID;
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            sp.ControllingClient.SendTeleportStart(teleportFlags);
            lookAt.Z = 0f;

            if(Math.Abs(lookAt.X) < 0.01f && Math.Abs(lookAt.Y) < 0.01f)
            {
                lookAt.X = 1.0f;
                lookAt.Y = 0;
            }

            sp.ControllingClient.SendLocalTeleport(position, lookAt, teleportFlags);
            sp.TeleportFlags = (Constants.TeleportFlags)teleportFlags;
            sp.RotateToLookAt(lookAt);
            sp.Velocity = Vector3.Zero;
            sp.Teleport(position);

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.ReceivedAtDestination);

            foreach (SceneObjectGroup grp in sp.GetAttachments())
            {
                if ((grp.ScriptEvents & scriptEvents.changed) != 0)
                    m_scene.EventManager.TriggerOnScriptChangedEvent(grp.LocalId, (uint)Changed.TELEPORT);
            }

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.CleaningUp);
        }

        /// <summary>
        /// Teleports the agent to a different region.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='regionHandle'>/param>
        /// <param name='position'></param>
        /// <param name='lookAt'></param>
        /// <param name='teleportFlags'></param>
        /// <param name='finalDestination'></param>
        private void TeleportAgentToDifferentRegion(
            ScenePresence sp, ulong regionHandle, Vector3 position,
            Vector3 lookAt, uint teleportFlags, out GridRegion finalDestination)
        {
            // Get destination region taking into account that the address could be an offset
            //     region inside a varregion.
            GridRegion reg = GetTeleportDestinationRegion(m_scene.GridService, m_sceneRegionInfo.ScopeID, regionHandle, ref position);

            if( reg == null)
            {
                finalDestination = null;

                // TP to a place that doesn't exist (anymore)
                // Inform the viewer about that
                sp.ControllingClient.SendTeleportFailed("The region you tried to teleport to was not found");

                // and set the map-tile to '(Offline)'
                Util.RegionHandleToRegionLoc(regionHandle, out uint regX, out uint regY);

                MapBlockData block = new()
                {
                    X = (ushort)(regX),
                    Y = (ushort)(regY),
                    Access = (byte)SimAccess.Down // == not there
                };

                List<MapBlockData> blocks = new() { block };
                sp.ControllingClient.SendMapBlock(blocks, 0);
                return;
            }

            string homeURI = m_scene.GetAgentHomeURI(sp.ControllingClient.AgentId);

            string reason = String.Empty;
            finalDestination = GetFinalDestination(reg, sp.ControllingClient.AgentId, homeURI, out _);

            if (finalDestination == null)
            {
                m_log.WarnFormat( "{0} Unable to teleport {1} {2}: {3}",
                                        LogHeader, sp.Name, sp.UUID, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                return;
            }

            if (!ValidateGenericConditions(sp, reg, finalDestination, teleportFlags, out _))
            {
                sp.ControllingClient.SendTeleportFailed(reason);
                return;
            }

            //
            // This is it
            //
            DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
        }

        // The teleport address could be an address in a subregion of a larger varregion.
        // Find the real base region and adjust the teleport location to account for the
        //    larger region.
        private GridRegion GetTeleportDestinationRegion(IGridService gridService, UUID scope, ulong regionHandle, ref Vector3 position)
        {
            Util.RegionHandleToWorldLoc(regionHandle, out uint x, out uint y);

            GridRegion reg;

            // handle legacy HG. linked regions are mapped into y = 0 and have no size information
            // so we can only search by base handle
            if( y == 0)
            {
                reg = gridService.GetRegionByPosition(scope, (int)x, (int)y);
                return reg;
            }

            // Compute the world location we're teleporting to
            double worldX = (double)x + position.X;
            double worldY = (double)y + position.Y;

            // Find the region that contains the position
            reg = GetRegionContainingWorldLocation(gridService, scope, worldX, worldY);

            if (reg != null)
            {
                // modify the position for the offset into the actual region returned
                position.X += x - reg.RegionLocX;
                position.Y += y - reg.RegionLocY;
            }

            return reg;
        }

        // Nothing to validate here
        protected virtual bool ValidateGenericConditions(ScenePresence sp, GridRegion reg, GridRegion finalDestination, uint teleportFlags, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Wraps DoTeleportInternal() and manages the transfer state.
        /// </summary>
        public void DoTeleport(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2} ({3}) {4}/{5} - agent is already in transit.",
                    sp.Name, sp.UUID, reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);
                sp.ControllingClient.SendTeleportFailed("Agent is already in transit.");
                return;
            }

            try
            {
                DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, m_sceneName, position, finalDestination.RegionName,
                    e.Message, e.StackTrace);

                sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(sp.UUID);
            }
        }

        /// <summary>
        /// Teleports the agent to another region.
        /// This method doesn't manage the transfer state; the caller must do that.
        /// </summary>
        private void DoTeleportInternal(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            if (reg == null || finalDestination == null)
            {
                sp.ControllingClient.SendTeleportFailed("Unable to locate destination");
                return;
            }

            string homeURI = m_scene.GetAgentHomeURI(sp.ControllingClient.AgentId);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleporting {0} {1} from {2} to {3} ({4}) {5}/{6}",
                sp.Name, sp.UUID, m_sceneName,
                reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);

            ulong destinationHandle = finalDestination.RegionHandle;

            if(destinationHandle == m_sceneRegionHandler)
            {
                sp.ControllingClient.SendTeleportFailed("Can't teleport to a region on same map position. Try going to another region first, then retry from there");
                return;
            }

            // Let's do DNS resolution only once in this process, please!
            // This may be a costly operation. The reg.ExternalEndPoint field is not a passive field,
            // it's actually doing a lot of work.
            IPEndPoint endPoint = finalDestination.ExternalEndPoint;
            if (endPoint == null || endPoint.Address == null)
            {
                sp.ControllingClient.SendTeleportFailed("Could not resolve destination Address");
                return;
            }

            if (!sp.ValidateAttachments())
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Failed validation of all attachments for teleport of {0} from {1} to {2}.  Continuing.",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName);

            EntityTransferContext ctx = new();
            if (!m_scene.SimulationService.QueryAccess(
                finalDestination, sp.UUID, homeURI, true, position, m_scene.GetFormatsOffered(), ctx, out string reason))
            {
                sp.ControllingClient.SendTeleportFailed(reason);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: {0} was stopped from teleporting from {1} to {2} because: {3}",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName, reason);

                return;
            }

            if (!sp.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                sp.ControllingClient.SendTeleportFailed(OutfitTPError);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: {0} was stopped from teleporting from {1} to {2} because: {3}",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName, "incompatible wearable");

                return;
            }

            // Before this point, teleport 'failure' is due to checkable pre-conditions such as whether the target
            // simulator can be found and is explicitly prepared to allow access.  Therefore, we will not count these
            // as server attempts.
            m_interRegionTeleportAttempts.Value++;

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: {0} transfer protocol version to {1} is {2} / {3}",
                sp.Scene.Name, finalDestination.RegionName, ctx.OutboundVersion, ctx.InboundVersion);

            // Fixing a bug where teleporting while sitting results in the avatar ending up removed from
            // both regions
            if (sp.IsSitting)
                sp.StandUp();
            else if (sp.Flying)
                teleportFlags |= (uint)TeleportFlags.IsFlying;

            sp.IsInLocalTransit = reg.RegionLocY != 0; // HG
            sp.IsInTransit = true;


            if (DisableInterRegionTeleportCancellation)
                teleportFlags |= (uint)TeleportFlags.DisableCancel;

            // At least on LL 3.3.4, this is not strictly necessary - a teleport will succeed without sending this to
            // the viewer.  However, it might mean that the viewer does not see the black teleport screen (untested).
            sp.ControllingClient.SendTeleportStart(teleportFlags);
  
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agentCircuit = sp.ControllingClient.RequestClientInfo();
            agentCircuit.startpos = position;
            agentCircuit.child = true;

            agentCircuit.Appearance = new() { AvatarHeight = sp.Appearance.AvatarHeight };

            if (currentAgentCircuit is not null)
            {
                agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                agentCircuit.Viewer = currentAgentCircuit.Viewer;
                agentCircuit.Channel = currentAgentCircuit.Channel;
                agentCircuit.Mac = currentAgentCircuit.Mac;
                agentCircuit.Id0 = currentAgentCircuit.Id0;
            }

            Util.RegionHandleToRegionLoc(destinationHandle, out uint newRegionX, out uint newRegionY);
            int oldSizeX = (int)m_sceneRegionInfo.RegionSizeX;
            int oldSizeY = (int)m_sceneRegionInfo.RegionSizeY;
            int newSizeX = finalDestination.RegionSizeX;
            int newSizeY = finalDestination.RegionSizeY;

            bool OutSideViewRange = !sp.IsInLocalTransit || NeedsNewAgent(sp.RegionViewDistance,
                m_sceneRegionInfo.RegionLocX, newRegionX, m_sceneRegionInfo.RegionLocY, newRegionY,
                oldSizeX, oldSizeY, newSizeX, newSizeY);

            if (OutSideViewRange)
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Determined that region {0} at {1},{2} size {3},{4} needs new child agent for agent {5} from {6}",
                    finalDestination.RegionName, newRegionX, newRegionY,newSizeX, newSizeY, sp.Name, m_sceneName);

                //sp.ControllingClient.SendTeleportProgress(teleportFlags, "Creating agent...");
                agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            }
            else
            {
                agentCircuit.CapsPath = sp.Scene.CapsModule.GetChildSeed(sp.UUID, reg.RegionHandle);
                agentCircuit.CapsPath ??= CapsUtil.GetRandomCapsObjectPath();
            }

            // We're going to fallback to V1 if the destination gives us anything smaller than 0.2
            if (ctx.OutboundVersion >= 0.2f)
                TransferAgent_V2(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, OutSideViewRange, lookAt, ctx, out _);
            else
                TransferAgent_V1(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, OutSideViewRange, lookAt, ctx, out _);
        }

        private void TransferAgent_V1(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, bool OutSideViewRange, Vector3 lookAt, EntityTransferContext ctx, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Using TP V1 for {0} going from {1} to {2}",
                sp.Name, m_sceneName, finalDestination.RegionName);

            string capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            List<ulong> childRegionsToClose = sp.GetChildAgentsToClose(destinationHandle, finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            if(agentCircuit.ChildrenCapSeeds != null)
            {
                foreach(ulong handler in childRegionsToClose)
                {
                    agentCircuit.ChildrenCapSeeds.Remove(handler);
                }
            }

            // Let's create an agent there if one doesn't exist yet.
            // NOTE: logout will always be false for a non-HG teleport.
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out bool logout))
            {
                m_interRegionTeleportFailures.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                sp.IsInTransit = false;
                return;
            }

            UUID spUUID = sp.UUID;
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            // OK, it got this agent. Let's close some child agents

            if (OutSideViewRange)
            {
                if (m_eqModule != null)
                {
                    // The EnableSimulator message makes the client establish a connection with the destination
                    // simulator by sending the initial UseCircuitCode UDP packet to the destination containing the
                    // correct circuit code.
                    m_eqModule.EnableSimulator(destinationHandle, endPoint, spUUID,
                                        finalDestination.RegionSizeX, finalDestination.RegionSizeY);
                    m_log.DebugFormat("{0} Sent EnableSimulator. regName={1}, size=<{2},{3}>", LogHeader,
                        finalDestination.RegionName, finalDestination.RegionSizeX, finalDestination.RegionSizeY);

                    // XXX: Is this wait necessary?  We will always end up waiting on UpdateAgent for the destination
                    // simulator to confirm that it has established communication with the viewer.
                    Thread.Sleep(200);

                    // At least on LL 3.3.4 for teleports between different regions on the same simulator this appears
                    // unnecessary - teleport will succeed and SEED caps will be requested without it (though possibly
                    // only on TeleportFinish).  This is untested for region teleport between different simulators
                    // though this probably also works.
                    m_eqModule.EstablishAgentCommunication(spUUID, endPoint, capsPath, finalDestination.RegionHandle,
                                        finalDestination.RegionSizeX, finalDestination.RegionSizeY);
                }
                else
                {
                    // XXX: This is a little misleading since we're information the client of its avatar destination,
                    // which may or may not be a neighbour region of the source region.  This path is probably little
                    // used anyway (with EQ being the one used).  But it is currently being used for test code.
                    sp.ControllingClient.InformClientOfNeighbour(destinationHandle, endPoint);
                }
            }

            // Let's send a full update of the agent. This is a synchronous call.
            AgentData agent = new();
            sp.CopyTo(agent,false);
            agent.SetLookAt(lookAt);

            if ((teleportFlags & (uint)TeleportFlags.IsFlying) != 0)
                agent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

            agent.Position = agentCircuit.startpos;
            SetCallbackURL(agent);

            // We will check for an abort before UpdateAgent since UpdateAgent will require an active viewer to
            // establish th econnection to the destination which makes it return true.
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} before UpdateAgent",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }

            // A common teleport failure occurs when we can send CreateAgent to the
            // destination region but the viewer cannot establish the connection (e.g. due to network issues between
            // the viewer and the destination).  In this case, UpdateAgent timesout after 10 seconds, although then
            // there's a further 10 second wait whilst we attempt to tell the destination to delete the agent in Fail().
            if (!UpdateAgent(reg, finalDestination, agent, sp, ctx))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                sp.IsInTransit = false;
                return;
            }

            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after UpdateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                CleanupFailedInterRegionTeleport(sp, currentAgentCircuit.SessionID.ToString(), finalDestination);
                sp.IsInTransit = false;
                return;
            }

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, m_sceneName, sp.Name);

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then
            // closes our existing agent which is still signalled as root.
            sp.IsChildAgent = true;

            // OK, send TPFinish to the client, so that it starts the process of contacting the destination region
            if (m_eqModule != null)
            {
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, spUUID,
                            finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            }
            else
            {
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);
            }

            // TeleportFinish makes the client send CompleteMovementIntoRegion (at the destination), which
            // trigers a whole shebang of things there, including MakeRoot. So let's wait for confirmation
            // that the client contacted the destination before we close things here.
            if (!m_entityTransferStateMachine.WaitForAgentArrivedAtDestination(spUUID))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after WaitForAgentArrivedAtDestination due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} to {1} from {2} failed due to no callback from destination region.  Returning avatar to source region.",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Destination region did not signal teleport completion.");
                sp.IsInTransit = false;
                return;
            }

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.CleaningUp);

            if(logout)
                sp.closeAllChildAgents();
            else
                sp.CloseChildAgents(childRegionsToClose);

            // call HG hook
            AgentHasMovedAway(sp, logout);

            sp.HasMovedAway(!(OutSideViewRange || logout));

             // Now let's make it officially a child agent
            sp.MakeChildAgent(destinationHandle);

            // Finally, let's close this previously-known-as-root agent, when the jump is outside the view zone

            if (NeedsClosing(reg, OutSideViewRange))
            {
                if (!m_scene.IncomingPreCloseClient(sp))
                    return;

                // We need to delay here because Imprudence viewers, unlike v1 or v3, have a short (<200ms, <500ms) delay before
                // they regard the new region as the current region after receiving the AgentMovementComplete
                // response.  If close is sent before then, it will cause the viewer to quit instead.
                //
                // This sleep can be increased if necessary.  However, whilst it's active,
                // an agent cannot teleport back to this region if it has teleported away.
                Thread.Sleep(2000);
                m_scene.CloseAgent(sp.UUID, false);
            }
            sp.IsInTransit = false;
        }

        private void TransferAgent_V2(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, bool OutSideViewRange, Vector3 lookAt, EntityTransferContext ctx, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;

            List<ulong> childRegionsToClose = null;
            // HG needs a deeper change
            bool localclose = (ctx.OutboundVersion < 0.7f || !sp.IsInLocalTransit);
            if (localclose)
            {
                childRegionsToClose = sp.GetChildAgentsToClose(destinationHandle, finalDestination.RegionSizeX, finalDestination.RegionSizeY);

                if(agentCircuit.ChildrenCapSeeds != null)
                {
                    foreach(ulong handler in childRegionsToClose)
                    {
                        agentCircuit.ChildrenCapSeeds.Remove(handler);
                    }
                }
            }

            if (OutSideViewRange && agentCircuit.ChildrenCapSeeds != null)
                agentCircuit.ChildrenCapSeeds.Remove(sp.RegionHandle);

            string capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);

            // Let's create an agent there if one doesn't exist yet.
            // NOTE: logout will always be false for a non-HG teleport.
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out bool logout))
            {
                m_interRegionTeleportFailures.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                sp.IsInTransit = false;
                return;
            }

            UUID spUUID = sp.UUID;
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                sp.IsInTransit = false;
                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                sp.IsInTransit = false;
                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then
            // closes our existing agent which is still signalled as root.
            //sp.IsChildAgent = true;

            // New protocol: send TP Finish directly, without prior ES or EAC. That's what happens in the Linden grid
            if (m_eqModule != null)
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, sp.UUID,
                                    finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            else
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, m_sceneName, sp.Name);

            // Let's send a full update of the agent.
            AgentData agent = new();
            sp.CopyTo(agent,false);
            agent.SetLookAt(lookAt);
            agent.Position = agentCircuit.startpos;

            if ((teleportFlags & (uint)TeleportFlags.IsFlying) != 0)
                agent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

            agent.SenderWantsToWaitForRoot = true;

            if(OutSideViewRange)
                SetNewCallbackURL(agent);

            // Reset the do not close flag.  This must be done before the destination opens child connections (here
            // triggered by UpdateAgent) to avoid race conditions.  However, we also want to reset it as late as possible
            // to avoid a situation where an unexpectedly early call to Scene.NewUserConnection() wrongly results
            // in no close.
            sp.DoNotCloseAfterTeleport = false;

            // we still need to flag this as child here
            // a close from receiving region seems possible to happen before we reach sp.MakeChildAgent below
            // causing the agent to be loggout out from grid incorrectly
            sp.IsChildAgent = true;
            // Send the Update. If this returns true, we know the client has contacted the destination
            // via CompleteMovementIntoRegion, so we can let go.
            // If it returns false, something went wrong, and we need to abort.
            if (!UpdateAgent(reg, finalDestination, agent, sp, ctx))
            {
                sp.IsChildAgent = false;
                if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, agentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                sp.IsInTransit = false;
                return;
            }

            //shut this up for now
            m_entityTransferStateMachine.ResetFromTransit(spUUID);

            //m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            sp.HasMovedAway(!(OutSideViewRange || logout));

            //HG hook
            AgentHasMovedAway(sp, logout);

            // Now let's make it officially a child agent
            sp.MakeChildAgent(destinationHandle);

            if(localclose)
            {
                if (logout)
                    sp.closeAllChildAgents();
                else
                    sp.CloseChildAgents(childRegionsToClose);
            }


            // if far jump we do need to close anyways
            if (NeedsClosing(reg, OutSideViewRange))
            {
                int count = 60;
                do
                {
                    Thread.Sleep(250);
                    if(sp.IsDeleted)
                        return;
                    if(!sp.IsInTransit)
                        break;
                } while (--count > 0);

                if (!sp.IsDeleted)
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Closing agent {0} in {1} after teleport {2}", sp.Name, m_sceneName, sp.IsInTransit?"timeout":"");
                    m_scene.CloseAgent(spUUID, false);
                }
                return;
            }
            // otherwise keep child
            sp.IsInTransit = false;
        }

        /// <summary>
        /// Clean up an inter-region teleport that did not complete, either because of simulator failure or cancellation.
        /// </summary>
        /// <remarks>
        /// All operations here must be idempotent so that we can call this method at any point in the teleport process
        /// up until we send the TeleportFinish event quene event to the viewer.
        /// <remarks>
        /// <param name='sp'> </param>
        /// <param name='finalDestination'></param>
        protected virtual void CleanupFailedInterRegionTeleport(ScenePresence sp, string auth_token, GridRegion finalDestination)
        {
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            if (sp.IsChildAgent) // We had set it to child before attempted TP (V1)
            {
                sp.IsChildAgent = false;
                ReInstantiateScripts(sp);

                EnableChildAgents(sp);
            }
            // Finally, kill the agent we just created at the destination.
            // XXX: Possibly this should be done asynchronously.
            Scene.SimulationService.CloseAgent(finalDestination, sp.UUID, auth_token);
        }

        /// <summary>
        /// Signal that the inter-region teleport failed and perform cleanup.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='finalDestination'></param>
        /// <param name='logout'></param>
        /// <param name='reason'>Human readable reason for teleport failure.  Will be sent to client.</param>
        protected virtual void Fail(ScenePresence sp, GridRegion finalDestination, bool logout, string auth_code, string reason)
        {
            CleanupFailedInterRegionTeleport(sp, auth_code, finalDestination);

            m_interRegionTeleportFailures.Value++;

            sp.ControllingClient.SendTeleportFailed(
                string.Format(
                    "Problems connecting to destination {0}, reason: {1}", finalDestination.RegionName, reason));

            sp.Scene.EventManager.TriggerTeleportFail(sp.ControllingClient, logout);
        }

        protected virtual bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason, out bool logout)
        {
            if (sp.GotAttachmentsData == false)
            {
                logout = false;
                reason = "Cannot leave region yet, attachments are still loading";
                return false;
            }

            GridRegion source = new(m_sceneRegionInfo)
            {
                RawServerURI = m_thisGridInfo.GateKeeperURL
            };

            logout = false;
            bool success = m_scene.SimulationService.CreateAgent(source, finalDestination, agentCircuit, teleportFlags, ctx, out reason);

            if (success)
                sp.Scene.EventManager.TriggerTeleportStart(sp.ControllingClient, reg, finalDestination, teleportFlags, logout);

            return success;
        }

        protected virtual bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agent, ScenePresence sp, EntityTransferContext ctx)
        {
            return m_scene.SimulationService.UpdateAgent(finalDestination, agent, ctx);
        }

        protected virtual void SetCallbackURL(AgentData agent)
        {
            agent.CallbackURI = m_sceneRegionInfo.ServerURI + "agent/" + agent.AgentID.ToString() + "/" + m_sceneRegionInfo.RegionID.ToString() + "/release/";

            //m_log.DebugFormat(
            //    "[ENTITY TRANSFER MODULE]: Set release callback URL to {0} in {1}",
            //    agent.CallbackURI, region.RegionName);
        }

        protected virtual void SetNewCallbackURL(AgentData agent)
        {
            agent.NewCallbackURI = m_sceneRegionInfo.ServerURI + "agent/" + agent.AgentID.ToString() + "/" + m_sceneRegionInfo.RegionID.ToString() + "/release/";

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Set release callback URL to {0} in {1}",
                agent.NewCallbackURI, m_sceneName);
        }

        /// <summary>
        /// Clean up operations once an agent has moved away through cross or teleport.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='logout'></param>
        ///
        /// now just a HG hook
        protected virtual void AgentHasMovedAway(ScenePresence sp, bool logout)
        {
//            if (sp.Scene.AttachmentsModule != null)
//                sp.Scene.AttachmentsModule.DeleteAttachmentsFromScene(sp, logout);
        }

        protected void KillEntity(Scene scene, uint localID)
        {
            scene.SendKillObject(new List<uint> { localID });
        }

        // HG hook
        protected virtual GridRegion GetFinalDestination(GridRegion region, UUID agentID, string agentHomeURI, out string message)
        {
            message = null;
            return region;
        }

        // This returns 'true' if the new region already has a child agent for our
        //    incoming agent. The implication is that, if 'false', we have to create  the
        //    child and then teleport into the region.
        protected virtual bool NeedsNewAgent(float viewdist, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY,
            int oldsizeX, int oldsizeY, int newsizeX, int newsizeY)
        {
            return Util.IsOutsideView(viewdist, oldRegionX, newRegionX, oldRegionY, newRegionY,
                    oldsizeX, oldsizeY, newsizeX, newsizeY);
        }

        // HG Hook
        protected virtual bool NeedsClosing(GridRegion reg, bool OutViewRange)

        {
            return OutViewRange;
        }

        #endregion

        #region Landmark Teleport
        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>

        public void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm)
        {
            RequestTeleportLandmark(remoteClient, lm, Vector3.Zero);
        }

        public virtual void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm, Vector3 lookAt)
        {
            if (lm == null || lm.Data == null || lm.Data.Length == 0)
                return;

            ScenePresence sp = m_scene.GetScenePresence(remoteClient.AgentId);
            if (sp == null || sp.IsDeleted || sp.IsInTransit || sp.IsChildAgent || sp.IsNPC)
                return;

            GridRegion info = m_scene.GridService.GetRegionByUUID(UUID.Zero, lm.RegionID);
            if (info == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("Landmark region not found");
                return;
            }
            //check if region on same position and fix local offset
            if (Util.CompareRegionHandles(lm.RegionHandle, lm.Position, info.RegionLocX, info.RegionLocY, info.RegionSizeX, info.RegionSizeY, out Vector3 offset))
            {
                m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, offset,
                    lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
            }
            else //region may had move to other grid slot. assume the lm position is good
                m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, lm.Position,
                    lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
        }

        #endregion

        #region Teleport Home

        public virtual void TriggerTeleportHome(UUID id, IClientAPI client)
        {
            TeleportHome(id, client);
        }

        public virtual bool TeleportHome(UUID id, IClientAPI client)
        {
            bool notsame = false;
            if (client == null)
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Request to teleport {0} home", id);
            }
            else
            {
                if (id.Equals(client.AgentId))
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.Name, id);
                }
                else
                {
                    notsame = true;
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Request to teleport {0} home by {1} {2}", id, client.Name, client.AgentId);
                }
            }

            ScenePresence sp = ((Scene)(client.Scene)).GetScenePresence(id);
            if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.ControllingClient == null || !sp.ControllingClient.IsActive)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent not found in the scene");
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Agent not found in the scene where it is supposed to be");
                return false;
            }

            IClientAPI targetClient = sp.ControllingClient;
            if (sp.IsInTransit)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent already processing a teleport");
                targetClient.SendTeleportFailed("Already processing a teleport");
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Agent still in teleport");
                return false;
            }

            //OpenSim.Services.Interfaces.PresenceInfo pinfo = Scene.PresenceService.GetAgent(client.SessionId);
            GridUserInfo uinfo = m_scene.GridUserService.GetGridUserInfo(id.ToString());
            if(uinfo == null)
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] Griduser info not found for {1}. Cannot send home.", id);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home region not found");
                targetClient.SendTeleportFailed("Your home region not found");
                return false;
            }

            if (uinfo.HomeRegionID.IsZero())
            {
                // can't find the Home region: Tell viewer and abort
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] no home set {0}", id);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home not set");
                targetClient.SendTeleportFailed("Home set not");
                return false;
            }

            GridRegion regionInfo = m_scene.GridService.GetRegionByUUID(UUID.Zero, uinfo.HomeRegionID);
            if (regionInfo == null)
            {
                // can't find the Home region: Tell viewer and abort
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] {0} home region {1} not found", id, uinfo.HomeRegionID);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home region not found");
                targetClient.SendTeleportFailed("Home region not found");
                return false;
            }

            Teleport(sp, regionInfo.RegionHandle, uinfo.HomePosition, uinfo.HomeLookAt,
                (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));

            return true;
        }

        #endregion


        #region Agent Crossings

        public bool checkAgentAccessToRegion(ScenePresence agent, GridRegion destiny, Vector3 position,
                EntityTransferContext ctx, out string reason)
        {
            reason = string.Empty;

            UUID agentID = agent.UUID;
            ulong destinyHandle = destiny.RegionHandle;

            if (m_bannedRegionCache.IfBanned(destinyHandle, agentID))
                return false;

            string homeURI = m_scene.GetAgentHomeURI(agentID);
            if (!m_scene.SimulationService.QueryAccess(destiny, agentID, homeURI, false, position,
                   m_scene.GetFormatsOffered(), ctx, out reason))
            {
                m_bannedRegionCache.Add(destinyHandle, agentID, 60.0);
                return false;
            }
            if (!agent.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                reason = OutfitTPError;
                m_bannedRegionCache.Add(destinyHandle, agentID, 60.0);
                return false;
            }

            return true;
        }


        // Given a position relative to the current region and outside of it
        // find the new region that the point is actually in
        // returns 'null' if new region not found or if agent as no access
        // else also returns new target position in the new region local coords
        // now only works for crossings

        public GridRegion GetDestination(UUID agentID, Vector3 pos,
                                            EntityTransferContext ctx, out Vector3 newpos, out string failureReason)
        {
            newpos = pos;
            failureReason = string.Empty;

//            m_log.DebugFormat(
//                "[ENTITY TRANSFER MODULE]: Crossing agent {0} at pos {1} in {2}", agent.Name, pos, scene.Name);

            // Compute world location of the agent's position
            double presenceWorldX = (double)m_sceneRegionInfo.WorldLocX + pos.X;
            double presenceWorldY = (double)m_sceneRegionInfo.WorldLocY + pos.Y;

            // Call the grid service to lookup the region containing the new position.
            GridRegion neighbourRegion = GetRegionContainingWorldLocation(
                                m_scene.GridService, m_sceneRegionInfo.ScopeID,
                                presenceWorldX, presenceWorldY);

            if (neighbourRegion == null)
                return null;
            if(neighbourRegion.RegionFlags != null && (neighbourRegion.RegionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                return null;

            if (m_bannedRegionCache.IfBanned(neighbourRegion.RegionHandle, agentID))
            {
                failureReason = "Access Denied or Temporary not possible";
                return null;
            }

            // Compute the entity's position relative to the new region
            newpos = new Vector3((float)(presenceWorldX - neighbourRegion.RegionLocX),
                                      (float)(presenceWorldY - neighbourRegion.RegionLocY),
                                      pos.Z);

            string homeURI = m_scene.GetAgentHomeURI(agentID);
           
            if (!m_scene.SimulationService.QueryAccess(
                    neighbourRegion, agentID, homeURI, false, newpos,
                    m_scene.GetFormatsOffered(), ctx, out failureReason))
            {
                // remember the fail
                m_bannedRegionCache.Add(neighbourRegion.RegionHandle, agentID, 60);
                if(string.IsNullOrWhiteSpace(failureReason))
                    failureReason = "Access Denied";
                return null;
            }
            return neighbourRegion;
        }

        public bool Cross(ScenePresence agent, bool isFlying)
        {
            ScenePresence ag = agent;
            ag.IsInLocalTransit = true;
            ag.IsInTransit = true;
            WorkManager.RunInThreadPool(delegate
            {
                CrossAsync(ag, isFlying);
                if (ag.IsDeleted)
                    return;
                if (!ag.IsChildAgent)
                {
                    // crossing failed
                    ag.CrossToNewRegionFail();
                }
                else
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", ag.Firstname, ag.Lastname);

                ag.IsInTransit = false;
            }, null,"AgentRegionCross-"+ag.UUID.ToString());
            return true;
        }

        public ScenePresence CrossAsync(ScenePresence agent, bool isFlying)
        {
            if(agent.RegionViewDistance == 0)
                return agent;

            EntityTransferContext ctx = new();

            // We need this because of decimal number parsing of the protocols.
            Culture.SetCurrentCulture();

            Vector3 pos = agent.AbsolutePosition + agent.Velocity * 0.2f;

            GridRegion neighbourRegion = GetDestination(agent.UUID, pos,
                                                            ctx, out Vector3 newpos, out string failureReason);
            if (neighbourRegion is null)
            {
                if (!agent.IsDeleted && failureReason != String.Empty && agent.ControllingClient != null)
                    agent.ControllingClient.SendAlertMessage(failureReason);
                return agent;
            }
            if (!agent.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                if (agent.ControllingClient is null)
                    agent.ControllingClient.SendAlertMessage(OutfitTPError);
                return agent;
            }

            //agent.IsInTransit = true;
            CrossAgentToNewRegionAsync(agent, newpos, neighbourRegion, isFlying, ctx);
            agent.IsInTransit = false;
            return agent;
        }

        public bool CrossAgentCreateFarChild(ScenePresence agent, GridRegion neighbourRegion, Vector3 pos, EntityTransferContext ctx)
        {
            ulong regionhandler = neighbourRegion.RegionHandle;
            if(agent.knowsNeighbourRegion(regionhandler))
                return true;

            GridRegion source = new(m_sceneRegionInfo);
            AgentCircuitData currentAgentCircuit = 
                    m_scene.AuthenticateHandler.GetAgentCircuitData(agent.ControllingClient.CircuitCode);
            AgentCircuitData agentCircuit = agent.ControllingClient.RequestClientInfo();
            agentCircuit.startpos = pos;
            agentCircuit.child = true;

            agentCircuit.Appearance = new() { AvatarHeight = agent.Appearance.AvatarHeight };

            if (currentAgentCircuit is not null)
            {
                agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                agentCircuit.Viewer = currentAgentCircuit.Viewer;
                agentCircuit.Channel = currentAgentCircuit.Channel;
                agentCircuit.Mac = currentAgentCircuit.Mac;
                agentCircuit.Id0 = currentAgentCircuit.Id0;
            }

            agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            agent.AddNeighbourRegion(neighbourRegion, agentCircuit.CapsPath);

            IPEndPoint endPoint = neighbourRegion.ExternalEndPoint;
            if(endPoint is null)
            {
                m_log.DebugFormat("CrossAgentCreateFarChild failed to resolve neighbour address {0}", neighbourRegion.ExternalHostName);
                return false;
            }
            if (!m_scene.SimulationService.CreateAgent(source, neighbourRegion, agentCircuit, (int)TeleportFlags.Default, ctx, out string _ ))
            {
                agent.RemoveNeighbourRegion(regionhandler);
                return false;
            }

            string capsPath = neighbourRegion.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            int newSizeX = neighbourRegion.RegionSizeX;
            int newSizeY = neighbourRegion.RegionSizeY;

            if (m_eqModule != null)
            {
                m_log.DebugFormat("{0} {1} is sending {2} EnableSimulator for neighbour region {3}(loc=<{4},{5}>,siz=<{6},{7}>) " +
                    "and EstablishAgentCommunication with seed cap {8}", LogHeader,
                    source.RegionName, agent.Name,
                    neighbourRegion.RegionName, neighbourRegion.RegionLocX, neighbourRegion.RegionLocY, newSizeX, newSizeY , capsPath);

                m_eqModule.EnableSimulator(regionhandler,
                        endPoint, agent.UUID, newSizeX, newSizeY);
                m_eqModule.EstablishAgentCommunication(agent.UUID, endPoint, capsPath,
                    regionhandler, newSizeX, newSizeY);
            }
            else
            {
                agent.ControllingClient.InformClientOfNeighbour(regionhandler, endPoint);
            }
            return true;
        }

        /// <summary>
        /// This Closes child agents on neighbouring regions
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        public ScenePresence CrossAgentToNewRegionAsync(
                                ScenePresence agent, Vector3 pos, GridRegion neighbourRegion,
                                bool isFlying, EntityTransferContext ctx)
        {
            try
            {
                m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: new region={1} at <{2},{3}>. newpos={4}",
                            LogHeader, neighbourRegion.RegionName, neighbourRegion.RegionLocX, neighbourRegion.RegionLocY, pos);

                if (neighbourRegion == null)
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: invalid destiny", LogHeader);
                    return agent;
                }

                IPEndPoint endpoint = neighbourRegion.ExternalEndPoint;
                if(endpoint == null)
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: failed to resolve neighbour address {0} ",neighbourRegion.ExternalHostName);
                    return agent;
                }

                m_entityTransferStateMachine.SetInTransit(agent.UUID);
                agent.RemoveFromPhysicalScene();

                if (!CrossAgentIntoNewRegionMain(agent, pos, neighbourRegion, endpoint, isFlying, ctx))
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: cross main failed. Resetting transfer state", LogHeader);
                    m_entityTransferStateMachine.ResetFromTransit(agent.UUID);
                }
            }
            catch (Exception e)
            {
                m_log.Error(string.Format("{0}: CrossAgentToNewRegionAsync: failed with exception  ", LogHeader), e);
            }
            return agent;
        }

        public bool CrossAgentIntoNewRegionMain(ScenePresence agent, Vector3 pos, GridRegion neighbourRegion,
                    IPEndPoint endpoint, bool isFlying, EntityTransferContext ctx)
        {
            int ts = Util.EnvironmentTickCount();
            bool sucess = true;
            string reason = String.Empty;
            List<ulong> childRegionsToClose = null;
            UUID agentUUID = agent.UUID;
            try
            {
                AgentData cAgent = new();
                agent.CopyTo(cAgent,true);

                cAgent.Position = pos;
                cAgent.ChildrenCapSeeds = agent.KnownRegions;

                if(ctx.OutboundVersion < 0.7f)
                {
                    childRegionsToClose = agent.GetChildAgentsToClose(neighbourRegion.RegionHandle, neighbourRegion.RegionSizeX, neighbourRegion.RegionSizeY);
                    if(cAgent.ChildrenCapSeeds != null)
                    {
                        foreach(ulong regh in childRegionsToClose)
                            cAgent.ChildrenCapSeeds.Remove(regh);
                    }
                }

                if (isFlying)
                    cAgent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

                // We don't need the callback anymnore
                cAgent.CallbackURI = String.Empty;

                // Beyond this point, extra cleanup is needed beyond removing transit state
                m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.Transferring);

                if (sucess && !m_scene.SimulationService.UpdateAgent(neighbourRegion, cAgent, ctx))
                {
                    sucess = false;
                    reason = "agent update failed";
                }

                if(!sucess)
                {
                    // region doesn't take it
                    m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.CleaningUp);

                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: agent {0} crossing to {1} failed: {2}",
                        agent.Name, neighbourRegion.RegionName, reason);

                    ReInstantiateScripts(agent);
                    if(agent.ParentID == 0 && agent.ParentUUID.IsZero())
                    {
                        agent.AddToPhysicalScene(isFlying);
                    }

                    return false;
                }

            m_log.DebugFormat("[CrossAgentIntoNewRegionMain] ok, time {0}ms",Util.EnvironmentTickCountSubtract(ts));
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Problem crossing user {0} to new region {1} from {2}.  Exception {3}{4}",
                    agent.Name, neighbourRegion.RegionName, m_sceneName, e.Message, e.StackTrace);

                // TODO: Might be worth attempting other restoration here such as reinstantiation of scripts, etc.
                return false;
            }

            if (!agent.KnownRegions.TryGetValue(neighbourRegion.RegionHandle, out string agentcaps))
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: No ENTITY TRANSFER MODULE information for region handle {0}, exiting CrossToNewRegion.",
                                 neighbourRegion.RegionHandle);
                return false;
            }

            // No turning back

            agent.IsChildAgent = true;

            string capsPath = neighbourRegion.ServerURI + CapsUtil.GetCapsSeedPath(agentcaps);

            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, agent.UUID);

            Vector3 vel2 = Vector3.Zero;
            if((agent.m_crossingFlags & 2) != 0)
                vel2 = new Vector3(agent.Velocity.X, agent.Velocity.Y, 0);

            if (m_eqModule != null)
            {
                m_eqModule.CrossRegion(
                    neighbourRegion.RegionHandle, pos, vel2 /* agent.Velocity */,
                    endpoint, capsPath, agentUUID, agent.ControllingClient.SessionId,
                    neighbourRegion.RegionSizeX, neighbourRegion.RegionSizeY);
            }
            else
            {
                m_log.ErrorFormat("{0} Using old CrossRegion packet. Varregion will not work!!", LogHeader);
                agent.ControllingClient.CrossRegion(neighbourRegion.RegionHandle, pos, agent.Velocity,
                        endpoint,capsPath);
            }

            // SUCCESS!
            m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.ReceivedAtDestination);

            // Unlike a teleport, here we do not wait for the destination region to confirm the receipt.
            m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.CleaningUp);

            if(childRegionsToClose != null)
                agent.CloseChildAgents(childRegionsToClose);

            if((agent.m_crossingFlags & 8) == 0)
                agent.ClearControls(); // don't let attachments delete (called in HasMovedAway) disturb taken controls on viewers

            agent.HasMovedAway((agent.m_crossingFlags & 8) == 0);

            agent.MakeChildAgent(neighbourRegion.RegionHandle);

            // FIXME: Possibly this should occur lower down after other commands to close other agents,
            // but not sure yet what the side effects would be.
            m_entityTransferStateMachine.ResetFromTransit(agentUUID);

            return true;
        }

        private void CrossAgentToNewRegionCompleted(IAsyncResult iar)
        {
            CrossAgentToNewRegionDelegate icon = (CrossAgentToNewRegionDelegate)iar.AsyncState;
            ScenePresence agent = icon.EndInvoke(iar);

            //// If the cross was successful, this agent is a child agent
            //if (agent.IsChildAgent)
            //    agent.Reset();
            //else // Not successful
            //    agent.RestoreInCurrentScene();

            // In any case
            agent.IsInTransit = false;

//            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", agent.Firstname, agent.Lastname);
        }

        #endregion

        #region Enable Child Agent

        /// <summary>
        /// This informs a single neighbouring region about agent "avatar", and avatar about it
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="region"></param>
        public void EnableChildAgent(ScenePresence sp, GridRegion region)
        {           
            int viewrange = (int)sp.RegionViewDistance;
            if(viewrange == 0)
                return;

            ICapabilitiesModule capsModule = m_scene.CapsModule;
            if (capsModule == null)
                return;

            Vector3 pos = sp.AbsolutePosition;

            int rtmp = region.RegionLocX - (int)m_sceneRegionInfo.WorldLocX - (int)pos.X;
            if ( rtmp > viewrange || rtmp < -(viewrange + region.RegionSizeX))
                return;
            rtmp = region.RegionLocY - (int)m_sceneRegionInfo.WorldLocY - (int)pos.Y;
            if (rtmp > viewrange || rtmp < -(viewrange + region.RegionSizeY))
                return;

            m_log.DebugFormat("[ENTITY TRANSFER]: Enabling child agent in new neighbour {0}", region.RegionName);

            ulong regionhandler = region.RegionHandle;

            Dictionary<ulong, string> seeds = new(capsModule.GetChildrenSeeds(sp.UUID));

            if (seeds.ContainsKey(regionhandler))
                seeds.Remove(regionhandler);

            if (!seeds.ContainsKey(m_sceneRegionHandler))
                seeds.Add(m_sceneRegionHandler, sp.ControllingClient.RequestClientInfo().CapsPath);

            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
            agent.BaseFolder = UUID.Zero;
            agent.InventoryFolder = UUID.Zero;
            agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, region);
            agent.startfar = sp.DrawDistance;
            agent.child = true;
            agent.Appearance = new AvatarAppearance
            {
                AvatarHeight = sp.Appearance.AvatarHeight
            };

            agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();

            seeds.Add(regionhandler, agent.CapsPath);

            agent.ChildrenCapSeeds = null;

            capsModule.SetChildrenSeed(sp.UUID, seeds);
            sp.KnownRegions = seeds;
            sp.AddNeighbourRegionSizeInfo(region);

            if (currentAgentCircuit != null)
            {
                agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agent.IPAddress = currentAgentCircuit.IPAddress;
                agent.Viewer = currentAgentCircuit.Viewer;
                agent.Channel = currentAgentCircuit.Channel;
                agent.Mac = currentAgentCircuit.Mac;
                agent.Id0 = currentAgentCircuit.Id0;
            }

            IPEndPoint external = region.ExternalEndPoint;
            if (external != null)
            {
                ScenePresence avatar = sp;
                GridRegion reg = region;
                WorkManager.RunInThreadPool(delegate
                {
                    InformClientOfNeighbourAsync(avatar, agent, reg, external, true);
                },"InformClientOfNeighbourAsync" + avatar.UUID.ToString());
            }
        }

        #endregion

        #region Enable Child Agents

        List<GridRegion> RegionsInView(Vector3 pos, RegionInfo curregion, List<GridRegion> fullneighbours, float viewrange)
        {
            if (fullneighbours.Count == 0 || viewrange == 0)
                return new List<GridRegion>();

            int itmp = (int)curregion.WorldLocX + (int)pos.X;
            int minX = itmp - (int)viewrange;
            int maxX = itmp + (int)viewrange;
            itmp = (int)curregion.WorldLocY + (int)pos.Y;
            int minY = itmp - (int)viewrange;
            int maxY = itmp + (int)viewrange;

            List<GridRegion> ret = new(fullneighbours.Count);
            foreach (GridRegion r in fullneighbours)
            {
                OpenSim.Framework.RegionFlags? regionFlags = r.RegionFlags;
                if (regionFlags != null)
                {
                    if ((regionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                        continue;
                }

                itmp = r.RegionLocX;
                if (maxX < itmp)
                    continue;
                if (minX > itmp + r.RegionSizeX)
                    continue;
                itmp = r.RegionLocY;
                if (maxY < itmp)
                    continue;
                if (minY > itmp + r.RegionSizeY)
                    continue;
                ret.Add(r);
            }
            return ret;
        }

        List<GridRegion> RegionsInSPView(ScenePresence sp)
        {
            int viewrange = (int)sp.RegionViewDistance;
            if (viewrange == 0)
                return new List<GridRegion>();

            List<GridRegion> fullneighbours = GetNeighbors(sp);
            if (fullneighbours.Count == 0)
                return new List<GridRegion>();

            Vector3 pos = sp.AbsolutePosition;
            int itmp = (int)m_sceneRegionInfo.WorldLocX + (int)pos.X;
            int minX = itmp - viewrange;
            int maxX = itmp + viewrange;
            itmp = (int)m_sceneRegionInfo.WorldLocY + (int)pos.Y;
            int minY = itmp - viewrange;
            int maxY = itmp + viewrange;
 
            List<GridRegion> ret = new(fullneighbours.Count);
            foreach (GridRegion r in fullneighbours)
            {
                OpenSim.Framework.RegionFlags? regionFlags = r.RegionFlags;
                if (regionFlags != null)
                {
                    if ((regionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                        continue;
                }

                itmp = r.RegionLocX;
                if (maxX < itmp)
                    continue;
                if (minX > itmp + r.RegionSizeX)
                    continue;
                itmp = r.RegionLocY;
                if (maxY < itmp)
                    continue;
                if (minY > itmp + r.RegionSizeY)
                    continue;
                ret.Add(r);
            }
            return ret;
        }

        /// <summary>
        /// This informs all neighbouring regions about agent "avatar".
        /// and as important informs the avatar about then
        /// </summary>
        /// <param name="sp"></param>
        public void EnableChildAgents(ScenePresence sp)
        {
            // assumes that out of view range regions are disconnected by the previous region
            ICapabilitiesModule capsModule = m_scene.CapsModule;
            if (capsModule == null)
                return;

            List<GridRegion> neighbours = RegionsInSPView(sp);

            LinkedList<ulong> previousRegionNeighbourHandles;
            Dictionary<ulong, string> seeds;

            seeds = new Dictionary<ulong, string>(capsModule.GetChildrenSeeds(sp.UUID));
            previousRegionNeighbourHandles = new LinkedList<ulong>(seeds.Keys);

            IClientAPI spClient = sp.ControllingClient;

            // This will fail if the user aborts login
            try
            {
                if (!seeds.ContainsKey(m_sceneRegionHandler))
                    seeds.Add(m_sceneRegionHandler, spClient.RequestClientInfo().CapsPath);
            }
            catch
            {
                return;
            }

            AgentCircuitData currentAgentCircuit =
                m_scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            List<AgentCircuitData> cagents = new();
            List<ulong> newneighbours = new();

            foreach (GridRegion neighbour in neighbours)
            {
                ulong handler = neighbour.RegionHandle;

                if (previousRegionNeighbourHandles.Contains(handler))
                {
                    // agent already knows this region
                    previousRegionNeighbourHandles.Remove(handler);
                    continue;
                }

                if (handler == m_sceneRegionHandler)
                    continue;

                // a new region to add
                AgentCircuitData agent = spClient.RequestClientInfo();
                agent.BaseFolder = UUID.Zero;
                agent.InventoryFolder = UUID.Zero;
                agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, neighbour);
                agent.child = true;
                agent.Appearance = new AvatarAppearance { AvatarHeight = sp.Appearance.AvatarHeight };
                agent.startfar = sp.DrawDistance;
                if (currentAgentCircuit is not null)
                {
                    agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                    agent.IPAddress = currentAgentCircuit.IPAddress;
                    agent.Viewer = currentAgentCircuit.Viewer;
                    agent.Channel = currentAgentCircuit.Channel;
                    agent.Mac = currentAgentCircuit.Mac;
                    agent.Id0 = currentAgentCircuit.Id0;
                }

                newneighbours.Add(handler);
                agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                seeds.Add(handler, agent.CapsPath);

                agent.ChildrenCapSeeds = null;
                cagents.Add(agent);
            }

            List<ulong> toclose;
            // previousRegionNeighbourHandles now contains regions to forget
            if (previousRegionNeighbourHandles.Count > 0)
            {
                if (previousRegionNeighbourHandles.Contains(m_sceneRegionHandler))
                    previousRegionNeighbourHandles.Remove(m_sceneRegionHandler);

                foreach (ulong handler in previousRegionNeighbourHandles)
                    seeds.Remove(handler);

                toclose = new List<ulong>(previousRegionNeighbourHandles);
            }
            else
                toclose = new List<ulong>();
            /// Update all child agent with everyone's seeds
                //            foreach (AgentCircuitData a in cagents)
                //                a.ChildrenCapSeeds = new Dictionary<ulong, string>(seeds);

            capsModule?.SetChildrenSeed(sp.UUID, seeds);

            sp.KnownRegions = seeds;
            sp.SetNeighbourRegionSizeInfo(neighbours);

            if (neighbours.Count > 0 || toclose.Count > 0)
            {
                AgentPosition agentpos = new()
                {
                    AgentID = new UUID(sp.UUID.Guid),
                    SessionID = spClient.SessionId,
                    Size = sp.Appearance.AvatarSize,
                    Center = sp.CameraPosition,
                    Far = sp.DrawDistance,
                    Position = sp.AbsolutePosition,
                    Velocity = sp.Velocity,
                    RegionHandle = m_sceneRegionHandler,
                    //agentpos.GodLevel = sp.GodLevel;
                    GodData = sp.GodController.State(),
                    Throttles = spClient.GetThrottlesPacked(1)
                };
                //agentpos.ChildrenCapSeeds = seeds;

                Util.FireAndForget(delegate
                {
                    int count = 0;
                    IPEndPoint ipe;
 
                    if(toclose.Count > 0)
                        sp.CloseChildAgents(toclose);

                    foreach (GridRegion neighbour in neighbours)
                    {
                        ulong handler = neighbour.RegionHandle;
                        try
                        {
                            if (newneighbours.Contains(handler))
                            {
                                ipe = neighbour.ExternalEndPoint;
                                if (ipe != null)
                                    InformClientOfNeighbourAsync(sp, cagents[count], neighbour, ipe, true);
                                else
                                {
                                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]:  lost DNS resolution for neighbour {0}", neighbour.ExternalHostName);
                                }
                                count++;
                            }
                            else if (!previousRegionNeighbourHandles.Contains(handler))
                            {
                                m_scene.SimulationService.UpdateAgent(neighbour, agentpos);
                            }
                            if (sp.IsDeleted)
                                return;
                        }
                        catch (Exception e)
                        {
                            m_log.ErrorFormat(
                                "[ENTITY TRANSFER MODULE]: Error creating child agent at {0} ({1} ({2}, {3}).  {4}",
                                neighbour.ExternalHostName,
                                neighbour.RegionHandle,
                                neighbour.RegionLocX,
                                neighbour.RegionLocY,
                                e);
                        }
                    }
                });
            }
        }

        public void CheckChildAgents(ScenePresence sp)
        {
            List<GridRegion> neighbours = RegionsInSPView(sp);

            Dictionary<ulong, string> previousRegionNeighbour = sp.KnownRegions;
            previousRegionNeighbour.Remove(m_sceneRegionHandler);

            IClientAPI spClient = sp.ControllingClient;
            AgentCircuitData currentAgentCircuit = m_scene.AuthenticateHandler.GetAgentCircuitData(spClient.CircuitCode);

            List<AgentCircuitData> cagents = new(neighbours.Count);
            List<GridRegion> newneighbours = new(neighbours.Count);

            foreach (GridRegion neighbour in neighbours)
            {
                ulong handler = neighbour.RegionHandle;

                if (previousRegionNeighbour.Remove(handler))
                {
                    // agent already knows this region
                    continue;
                }

                if (handler == m_sceneRegionHandler)
                    continue;

                // a new region to add
                AgentCircuitData agent = spClient.RequestClientInfo();
                agent.BaseFolder = UUID.Zero;
                agent.InventoryFolder = UUID.Zero;
                agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, neighbour);
                agent.child = true;
                agent.Appearance = new AvatarAppearance { AvatarHeight = sp.Appearance.AvatarHeight };
                agent.startfar = sp.DrawDistance;
                if (currentAgentCircuit is not null)
                {
                    agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                    agent.IPAddress = currentAgentCircuit.IPAddress;
                    agent.Viewer = currentAgentCircuit.Viewer;
                    agent.Channel = currentAgentCircuit.Channel;
                    agent.Mac = currentAgentCircuit.Mac;
                    agent.Id0 = currentAgentCircuit.Id0;
                }

                newneighbours.Add(neighbour);
                agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                sp.AddNeighbourRegion(neighbour, agent.CapsPath);

                agent.ChildrenCapSeeds = null;
                cagents.Add(agent);
            }

            // previousRegionNeighbourHandles now contains regions to forget
            if (previousRegionNeighbour.Count > 0)
            {
                List<ulong> toclose = new(previousRegionNeighbour.Keys);
                sp.CloseChildAgents(toclose);
            }
 
            ICapabilitiesModule capsModule = m_scene.CapsModule;
            capsModule?.SetChildrenSeed(sp.UUID, sp.KnownRegions);

            if (newneighbours.Count > 0)
            {
                int count = 0;
                IPEndPoint ipe;

                foreach (GridRegion neighbour in newneighbours)
                {
                    try
                    {
                        ipe = neighbour.ExternalEndPoint;
                        if (ipe != null)
                            InformClientOfNeighbourAsync(sp, cagents[count], neighbour, ipe, true);
                        else
                        {
                            m_log.DebugFormat("[ENTITY TRANSFER MODULE]:  lost DNS resolution for neighbour {0}", neighbour.ExternalHostName);
                        }
                        count++;
                        if (sp.IsDeleted)
                            return;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Error creating child agent at {0} ({1} ({2}, {3}).  {4}",
                            neighbour.ExternalHostName,
                            neighbour.RegionHandle,
                            neighbour.RegionLocX,
                            neighbour.RegionLocY,
                            e);
                    }
                }
            }
        }

        public void CloseOldChildAgents(ScenePresence sp)
        {
            Dictionary<ulong, string> seeds = sp.KnownRegions;
            if (seeds.Count == 0)
                return;

            seeds.Remove(m_sceneRegionHandler);
            if (seeds.Count == 0)
                return;

            List<GridRegion> neighbours = RegionsInSPView(sp);
            sp.SetNeighbourRegionSizeInfo(neighbours);
            foreach (GridRegion neighbour in neighbours)
                seeds.Remove(neighbour.RegionHandle);

            // seeds now contains regions to forget
            if (seeds.Count == 0)
                return;

            List<ulong> toclose = new(seeds.Keys);
            Util.FireAndForget(delegate
                {
                    sp.CloseChildAgents(toclose);
                });
        }

        // Computes the difference between two region bases.
        // Returns a vector of world coordinates (meters) from base of first region to the second.
        // The first region is the home region of the passed scene presence.
        Vector3 CalculateOffset(ScenePresence sp, GridRegion neighbour)
        {
              return new Vector3(sp.Scene.RegionInfo.WorldLocX - neighbour.RegionLocX,
                                sp.Scene.RegionInfo.WorldLocY - neighbour.RegionLocY,
                                0f);
        }
        #endregion

        #region NotFoundLocationCache class
        // A collection of not found locations to make future lookups 'not found' lookups quick.
        // A simple expiring cache that keeps not found locations for some number of seconds.
        // A 'not found' location is presumed to be anywhere in the minimum sized region that
        //    contains that point. A conservitive estimate.
        private class NotFoundLocationCache
        {
            private readonly Dictionary<ulong, DateTime> m_notFoundLocations = new();
            public NotFoundLocationCache()
            {
            }
            // just use normal regions handlers and sizes
            public void Add(double pX, double pY)
            {
                ulong psh = (ulong)pX & 0xffffff00ul;
                psh <<= 32;
                psh |= (ulong)pY & 0xffffff00ul;

                lock (m_notFoundLocations)
                    m_notFoundLocations[psh] = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            }
            // Test to see of this point is in any of the 'not found' areas.
            // Return 'true' if the point is found inside the 'not found' areas.
            public bool Contains(double pX, double pY)
            {
                ulong psh = (ulong)pX & 0xffffff00ul;
                psh <<= 32;
                psh |= (ulong)pY & 0xffffff00ul;

                lock (m_notFoundLocations)
                {
                    if(m_notFoundLocations.ContainsKey(psh))
                    {
                        if(m_notFoundLocations[psh] > DateTime.UtcNow)
                            return true;
                        m_notFoundLocations.Remove(psh);
                    }
                    return false;
                }
            }

            private void DoExpiration()
            {
                List<ulong> m_toRemove = new();
                DateTime now = DateTime.UtcNow;
                lock (m_notFoundLocations)
                {
                    foreach (KeyValuePair<ulong, DateTime> kvp in m_notFoundLocations)
                    {
                        if (kvp.Value < now)
                            m_toRemove.Add(kvp.Key);
                    }

                    if (m_toRemove.Count > 0)
                    {
                        foreach (ulong u in m_toRemove)
                            m_notFoundLocations.Remove(u);
                        m_toRemove.Clear();
                    }
                }
            }
        }

        #endregion // NotFoundLocationCache class
        #region getregions
        private readonly NotFoundLocationCache m_notFoundLocationCache = new();

        protected GridRegion GetRegionContainingWorldLocation(IGridService pGridService, UUID pScopeID, double px, double py)
        {
         // Given a world position, get the GridRegion info for
         //   the region containing that point.

            // check if we already found it does not exist
            if (m_notFoundLocationCache.Contains(px, py))
                return null;

            // reduce to next grid corner
            // this is all that is needed on 0.9 grids
            uint possibleX = (uint)px & 0xffffff00u;
            uint possibleY = (uint)py & 0xffffff00u;
            GridRegion ret = pGridService.GetRegionByPosition(pScopeID, (int)possibleX, (int)possibleY);
            if (ret != null)
                return ret;
 
            /* obsolete code
            // for 0.8 regions just make a BIG area request. old code whould do it plus 4 more smaller on region open edges
            // this is what 0.9 grids now do internally
            List<GridRegion> possibleRegions = pGridService.GetRegionRange(pScopeID,
                        (int)(px - Constants.MaximumRegionSize), (int)(px + 1), // +1 bc left mb not part of range
                        (int)(py - Constants.MaximumRegionSize), (int)(py + 1));
            if (possibleRegions != null && possibleRegions.Count > 0)
            {
                // If we found some regions, check to see if the point is within
                foreach (GridRegion gr in possibleRegions)
                {
                    if (px >= (double)gr.RegionLocX && px < (double)(gr.RegionLocX + gr.RegionSizeX)
                                && py >= (double)gr.RegionLocY && py < (double)(gr.RegionLocY + gr.RegionSizeY))
                    {
                        // Found a region that contains the point
                        return gr;
                    }
                }
            }
            */

            // remember this location was not found so we can quickly not find it next time
            m_notFoundLocationCache.Add(px, py);
            return null;
        }

        /// <summary>
        /// Async component for informing client of which neighbours exist
        /// </summary>
        /// <remarks>
        /// This needs to run asynchronously, as a network timeout may block the thread for a long while
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="a"></param>
        /// <param name="regionHandle"></param>
        /// <param name="endPoint"></param>
        private void InformClientOfNeighbourAsync(ScenePresence sp, AgentCircuitData agentCircData, GridRegion reg,
                                                  IPEndPoint endPoint, bool newAgent)
        {
            if (newAgent)
            {
                // we may already had lost this sp
                if(sp == null || sp.IsDeleted || sp.ControllingClient == null) // something bad already happened
                   return;

                Scene scene = sp.Scene;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Informing {0} {1} about neighbour {2} {3} at ({4},{5})",
                    sp.Name, sp.UUID, reg.RegionName, endPoint, reg.RegionCoordX, reg.RegionCoordY);

                string capsPath = reg.ServerURI + CapsUtil.GetCapsSeedPath(agentCircData.CapsPath);

                bool regionAccepted = scene.SimulationService.CreateAgent(reg, reg, agentCircData, (uint)TeleportFlags.Default, null, out string reason);

                if (regionAccepted)
                {
                    // give  time for createAgent to finish, since it is async and does grid services access
                    Thread.Sleep(500);

                    if (m_eqModule != null)
                    {
                        if(sp == null || sp.IsDeleted || sp.ControllingClient == null) // something bad already happened
                            return;

                        m_log.DebugFormat("{0} {1} is sending {2} EnableSimulator for neighbour region {3}(loc=<{4},{5}>,siz=<{6},{7}>) " +
                            "and EstablishAgentCommunication with seed cap {8}", LogHeader,
                            scene.RegionInfo.RegionName, sp.Name,
                            reg.RegionName, reg.RegionLocX, reg.RegionLocY, reg.RegionSizeX, reg.RegionSizeY, capsPath);

                        m_eqModule.EnableSimulator(reg.RegionHandle, endPoint, sp.UUID, reg.RegionSizeX, reg.RegionSizeY);
                        m_eqModule.EstablishAgentCommunication(sp.UUID, endPoint, capsPath, reg.RegionHandle, reg.RegionSizeX, reg.RegionSizeY);
                    }
                    else
                    {
                        sp.ControllingClient.InformClientOfNeighbour(reg.RegionHandle, endPoint);
                        // TODO: make Event Queue disablable!
                    }

                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Completed inform {0} {1} about neighbour {2}", sp.Name, sp.UUID, endPoint);
                }

                else
                {
                    sp.RemoveNeighbourRegion(reg.RegionHandle);
                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: Region {0} did not accept {1} {2}: {3}",
                        reg.RegionName, sp.Name, sp.UUID, reason);
                }
            }

        }

        // all this code should be moved to scene replacing the now bad one there
        // cache Neighbors
        List<GridRegion> Neighbors = null;
        DateTime LastNeighborsTime = DateTime.MinValue;

        /// <summary>
        /// Return the list of online regions that are considered to be neighbours to the given scene.
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="pRegionLocX"></param>
        /// <param name="pRegionLocY"></param>
        /// <returns></returns>
        protected List<GridRegion> GetNeighbors(ScenePresence avatar)
        {
            if (Neighbors != null && (DateTime.UtcNow - LastNeighborsTime).TotalSeconds < 30)
            {
                return Neighbors;
            }

            Scene pScene = avatar.Scene;
            uint dd = (uint)pScene.MaxRegionViewDistance;
            if(dd <= 1)
                return new List<GridRegion>();

            RegionInfo regionInfo = pScene.RegionInfo;
            uint startX = regionInfo.WorldLocX;
            uint endX = startX + regionInfo.RegionSizeX;
            uint startY = regionInfo.WorldLocY;
            uint endY = startY + regionInfo.RegionSizeY;

            --dd;
            startX -= dd;
            startY -= dd;
            endX += dd;
            endY += dd;

            List<GridRegion> neighbours = avatar.Scene.GridService.GetRegionRange(
                    regionInfo.ScopeID, (int)startX, (int)endX, (int)startY, (int)endY);

            // The r.RegionFlags == null check only needs to be made for simulators before 2015-01-14 (pre 0.8.1).
            neighbours.RemoveAll( r => r.RegionID.Equals(regionInfo.RegionID));
            Neighbors = neighbours;
            LastNeighborsTime = DateTime.UtcNow;
            return neighbours;
        }
        #endregion

        #region Agent Arrived

        public void AgentArrivedAtDestination(UUID id)
        {
            ScenePresence sp = m_scene.GetScenePresence(id);
            if(sp == null || sp.IsDeleted || !sp.IsInTransit)
                return;

            //Scene.CloseAgent(sp.UUID, false);
            sp.IsInTransit = false;
            //m_entityTransferStateMachine.SetAgentArrivedAtDestination(id);
        }

        #endregion

        #region Object Transfers

        public GridRegion GetObjectDestination(SceneObjectGroup grp, Vector3 targetPosition, out Vector3 newpos)
        {
            newpos = targetPosition;

            Scene scene = grp.Scene;
            if (scene == null)
                return null;

            int x = (int)targetPosition.X + (int)scene.RegionInfo.WorldLocX;
            if (targetPosition.X >= 0)
                x++;
            else
                x--;

            int y = (int)targetPosition.Y + (int)scene.RegionInfo.WorldLocY;
            if (targetPosition.Y >= 0)
                y++;
            else
                y--;

            GridRegion neighbourRegion = scene.GridService.GetRegionByPosition(scene.RegionInfo.ScopeID,x,y);
            if (neighbourRegion == null)
            {
                return null;
            }

            float newRegionSizeX = neighbourRegion.RegionSizeX;
            float newRegionSizeY = neighbourRegion.RegionSizeY;
            if (newRegionSizeX == 0)
                newRegionSizeX = Constants.RegionSize;
            if (newRegionSizeY == 0)
                newRegionSizeY = Constants.RegionSize;

            newpos.X = targetPosition.X - (neighbourRegion.RegionLocX - (int)scene.RegionInfo.WorldLocX);
            newpos.Y = targetPosition.Y - (neighbourRegion.RegionLocY - (int)scene.RegionInfo.WorldLocY);

            const float enterDistance = 0.2f;
            newpos.X = Utils.Clamp(newpos.X, enterDistance, newRegionSizeX - enterDistance);
            newpos.Y = Utils.Clamp(newpos.Y, enterDistance, newRegionSizeY - enterDistance);

            return neighbourRegion;
        }

        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// FIMXE: we still return true if the crossing object was not successfully deleted from the originating region
        /// </returns>
        public bool CrossPrimGroupIntoNewRegion(GridRegion destination, Vector3 newPosition, SceneObjectGroup grp, bool silent, bool removeScripts)
        {
            //m_log.Debug("  >>> CrossPrimGroupIntoNewRegion <<<");

            Culture.SetCurrentCulture();

            bool successYN = false;
            grp.RootPart.ClearUpdateSchedule();
            //int primcrossingXMLmethod = 0;

            if (destination != null)
            {
                if (m_scene.SimulationService != null)
                    successYN = m_scene.SimulationService.CreateObject(destination, newPosition, grp, true);

                if (successYN)
                {
                    // We remove the object here
                    try
                    {
                        grp.Scene.DeleteSceneObject(grp, silent, removeScripts);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
            }
            else
            {
                m_log.Error("[ENTITY TRANSFER MODULE]: destination was unexpectedly null in Scene.CrossPrimGroupIntoNewRegion()");
            }

            return successYN;
        }

        #endregion

        #region Misc

        public bool IsInTransit(UUID id)
        {
            return m_entityTransferStateMachine.GetAgentTransferState(id) != null;
        }

        protected void ReInstantiateScripts(ScenePresence sp)
        {
            int i = 0;
            if (sp.InTransitScriptStates.Count > 0)
            {
                List<SceneObjectGroup> attachments = sp.GetAttachments();

                foreach (SceneObjectGroup sog in attachments)
                {
                    if (i < sp.InTransitScriptStates.Count)
                    {
                        sog.SetState(sp.InTransitScriptStates[i++], sp.Scene);
                        sog.CreateScriptInstances(0, false, sp.Scene.DefaultScriptEngine, -1);
                        sog.ResumeScripts();
                    }
                    else
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: InTransitScriptStates.Count={0} smaller than Attachments.Count={1}",
                            sp.InTransitScriptStates.Count, attachments.Count);
                }

                sp.InTransitScriptStates.Clear();
            }
        }
        #endregion

        public virtual bool HandleIncomingSceneObject(SceneObjectGroup so, Vector3 newPosition)
        {
            if (so.OwnerID.IsZero())
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied object {0}({1}) entry into {2} because ownerID is zero",
                        so.Name, so.UUID, m_sceneName);
                return false;
            }

            // If the user is banned, we won't let any of their objects
            // enter. Period.
            if (!m_scene.Permissions.IsAdministrator(so.OwnerID) && m_sceneRegionInfo.EstateSettings.IsBanned(so.OwnerID))
            {
                m_log.Debug(
                    $"[ENTITY TRANSFER MODULE]: Denied {so.Name} {so.UUID} into { m_sceneName} of banned owner {so.OwnerID}");
                return false;
            }

            if(so.IsAttachmentCheckFull())
            {
                if(m_scene.GetScenePresence(so.OwnerID) == null)
                {
                    m_log.Debug(
                        $"[ENTITY TRANSFER MODULE]: Denied attachment {so.Name}({so.UUID}) owner {so.OwnerID} not in region {m_sceneName}");
                    return false;
                }
            }

            if (!newPosition.IsZero())
                so.RootPart.GroupPosition = newPosition;

            if (!m_scene.AddSceneObject(so))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Problem adding scene object {0} {1} into {2} ",
                    so.Name, so.UUID, m_sceneName);

                return false;
            }

            if (!so.IsAttachment)
            {
                // FIXME: It would be better to never add the scene object at all rather than add it and then delete
                // it
                if (!m_scene.Permissions.CanObjectEntry(so, true, so.AbsolutePosition))
                {
                    // Deny non attachments based on parcel settings
                    //
                    m_log.Info("[ENTITY TRANSFER MODULE]: Denied prim crossing because of parcel settings");

                    m_scene.DeleteSceneObject(so, false);

                    return false;
                }

                // For attachments, we need to wait until the agent is root
                // before we restart the scripts, or else some functions won't work.
                so.RootPart.ParentGroup.CreateScriptInstances(0, false, m_scene.DefaultScriptEngine, GetStateSource(so));

                so.ResumeScripts();

                // AddSceneObject already does this and doing it again messes
                //if (so.RootPart.KeyframeMotion != null)
                //    so.RootPart.KeyframeMotion.UpdateSceneObject(so);
            }

            return true;
        }

        public virtual bool HandleIncomingAttachments(ScenePresence sp, List<SceneObjectGroup> attachments)
        {
            if (sp.IsDeleted)
                return false;

            if (m_sceneRegionInfo.EstateSettings.IsBanned(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied Attachments for banned avatar {0}", sp.Name);
                return false;
            }

            foreach(SceneObjectGroup so in attachments)
            {
                if (!m_scene.AddSceneObject(so))
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Problem adding attachment {0} {1} into {2} ",
                        so.Name, so.UUID, m_sceneName);
                    continue;
                }
            }

            sp.GotAttachmentsData = true;
            return true;
        }

        public int GetStateSource(SceneObjectGroup sog)
        {
            ScenePresence sp = m_scene.GetScenePresence(sog.OwnerID);

            if (sp != null)
                return sp.GetStateSource();

            return 2; // StateSource.PrimCrossing
        }
    }
}
