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
using System.Linq;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

using Mono.Addins;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.World.Permissions
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DefaultPermissionsModule")]
    public class DefaultPermissionsModule : INonSharedRegionModule, IPermissionsModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene m_scene;
        protected ScenePermissions scenePermissions;
        protected bool m_Enabled;

        private InventoryFolderImpl m_libraryRootFolder;
        protected InventoryFolderImpl LibraryRootFolder
        {
            get
            {
                if (m_libraryRootFolder is null)
                {
                    ILibraryService lib = m_scene.RequestModuleInterface<ILibraryService>();
                    if (lib is not null)
                    {
                        m_libraryRootFolder = lib.LibraryRootFolder;
                    }
                }
                return m_libraryRootFolder;
            }
        }

        #region Constants
        /// <value>
        /// Different user set names that come in from the configuration file.
        /// </value>
        enum UserSet
        {
            All,
            Administrators
        };

        #endregion

        #region Bypass Permissions / Debug Permissions Stuff

        // Bypasses the permissions engine
        private bool m_bypassPermissions = true;
        private bool m_bypassPermissionsValue = true;
        private bool m_propagatePermissions = false;
        private bool m_debugPermissions = false;
        private bool m_allowGridAdmins = false;
        private bool m_forceAdminModeAlwaysOn;
        private bool m_allowAdminActionsWithoutGodMode;
        private bool m_takeCopyRestricted = false;

        /// <value>
        /// The set of users that are allowed to create scripts.  This is only active if permissions are not being
        /// bypassed.  This overrides normal permissions.
        /// </value>
        private UserSet m_allowedScriptCreators = UserSet.All;

        /// <value>
        /// The set of users that are allowed to edit (save) scripts.  This is only active if
        /// permissions are not being bypassed.  This overrides normal permissions.-
        /// </value>
        private UserSet m_allowedScriptEditors = UserSet.All;

        private readonly Dictionary<string, bool> GrantLSL = new();
        private readonly Dictionary<string, bool> GrantCS = new();
        private readonly Dictionary<string, bool> GrantVB = new();
        private readonly Dictionary<string, bool> GrantJS = new();
        private readonly Dictionary<string, bool> GrantYP = new();

        private IFriendsModule m_friendsModule;
        private IFriendsModule FriendsModule
        {
            get
            {
                m_friendsModule ??= m_scene.RequestModuleInterface<IFriendsModule>();
                return m_friendsModule;
            }
        }
        private IGroupsModule m_groupsModule;
        private IGroupsModule GroupsModule
        {
            get
            {
                m_groupsModule ??= m_scene.RequestModuleInterface<IGroupsModule>();
                return m_groupsModule;
            }
        }

        private IMoapModule m_moapModule;
        private IMoapModule MoapModule
        {
            get
            {
                m_moapModule ??= m_scene.RequestModuleInterface<IMoapModule>();
                return m_moapModule;
            }
        }
        #endregion

        #region INonSharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            string permissionModules = Util.GetConfigVarFromSections<string>(config, "permissionmodules",
                new string[] { "Startup", "Permissions" }, "DefaultPermissionsModule");

            List<string> modules = new(permissionModules.Split(',').Select(m => m.Trim()));

            if (!modules.Contains("DefaultPermissionsModule"))
                return;

            m_Enabled = true;

            string[] sections = new string[] { "Startup", "Permissions" };

            m_allowGridAdmins = Util.GetConfigVarFromSections<bool>(config, "allow_grid_gods", sections, true);
            m_bypassPermissions = !Util.GetConfigVarFromSections<bool>(config, "serverside_object_permissions", sections, true);
            m_propagatePermissions = Util.GetConfigVarFromSections<bool>(config, "propagate_permissions", sections, true);

            m_forceAdminModeAlwaysOn = Util.GetConfigVarFromSections<bool>(config, "automatic_gods", sections, false);
            m_allowAdminActionsWithoutGodMode = Util.GetConfigVarFromSections<bool>(config, "implicit_gods", sections, false);
            if(m_allowAdminActionsWithoutGodMode)
                m_forceAdminModeAlwaysOn = false;

            m_allowedScriptCreators
                = ParseUserSetConfigSetting(config, "allowed_script_creators", m_allowedScriptCreators);
            m_allowedScriptEditors
                = ParseUserSetConfigSetting(config, "allowed_script_editors", m_allowedScriptEditors);

            if (m_bypassPermissions)
                m_log.Info("[PERMISSIONS]: serverside_object_permissions = false in ini file so disabling all region service permission checks");
            else
                m_log.Debug("[PERMISSIONS]: Enabling all region service permission checks");

            m_takeCopyRestricted = Util.GetConfigVarFromSections<bool>(config, "take_copy_restricted", sections, false);

            string grant = Util.GetConfigVarFromSections<string>(config, "GrantLSL",
                new string[] { "Startup", "Permissions" }, string.Empty);
            if (grant.Length > 0)
            {
                foreach (string uuidl in grant.Split(','))
                {
                    string uuid = uuidl.Trim(" \t".ToCharArray());
                    GrantLSL.Add(uuid, true);
                }
            }

            grant = Util.GetConfigVarFromSections<string>(config, "GrantCS",
                new string[] { "Startup", "Permissions" }, string.Empty);
            if (grant.Length > 0)
            {
                foreach (string uuidl in grant.Split(','))
                {
                    string uuid = uuidl.Trim(" \t".ToCharArray());
                    GrantCS.Add(uuid, true);
                }
            }

            grant = Util.GetConfigVarFromSections<string>(config, "GrantVB",
                new string[] { "Startup", "Permissions" }, string.Empty);
            if (grant.Length > 0)
            {
                foreach (string uuidl in grant.Split(','))
                {
                    string uuid = uuidl.Trim(" \t".ToCharArray());
                    GrantVB.Add(uuid, true);
                }
            }

            grant = Util.GetConfigVarFromSections<string>(config, "GrantJS",
                new string[] { "Startup", "Permissions" }, string.Empty);
            if (grant.Length > 0)
            {
                foreach (string uuidl in grant.Split(','))
                {
                    string uuid = uuidl.Trim(" \t".ToCharArray());
                    GrantJS.Add(uuid, true);
                }
            }

            grant = Util.GetConfigVarFromSections<string>(config, "GrantYP",
                new string[] { "Startup", "Permissions" }, string.Empty);
            if (grant.Length > 0)
            {
                foreach (string uuidl in grant.Split(','))
                {
                    string uuid = uuidl.Trim(" \t".ToCharArray());
                    GrantYP.Add(uuid, true);
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;

            scene.RegisterModuleInterface<IPermissionsModule>(this);
            scenePermissions = m_scene.Permissions;

            //Register functions with Scene External Checks!
            scenePermissions.OnBypassPermissions += BypassPermissions;
            scenePermissions.OnSetBypassPermissions += SetBypassPermissions;
            scenePermissions.OnPropagatePermissions += PropagatePermissions;

            scenePermissions.OnIsGridGod += IsGridAdministrator;
            scenePermissions.OnIsAdministrator += IsAdministrator;
            scenePermissions.OnIsEstateManager += IsEstateManager;

            scenePermissions.OnGenerateClientFlags += GenerateClientFlags;

            scenePermissions.OnIssueEstateCommand += CanIssueEstateCommand;
            scenePermissions.OnRunConsoleCommand += CanRunConsoleCommand;

            scenePermissions.OnTeleport += CanTeleport;

            scenePermissions.OnInstantMessage += CanInstantMessage;

            scenePermissions.OnAbandonParcel += CanAbandonParcel;
            scenePermissions.OnReclaimParcel += CanReclaimParcel;
            scenePermissions.OnDeedParcel += CanDeedParcel;
            scenePermissions.OnSellParcel += CanSellParcel;
            scenePermissions.OnEditParcelProperties += CanEditParcelProperties;
            scenePermissions.OnTerraformLand += CanTerraformLand;
            scenePermissions.OnBuyLand += CanBuyLand;

            scenePermissions.OnReturnObjects += CanReturnObjects;

            scenePermissions.OnRezObject += CanRezObject;
            scenePermissions.OnObjectEntry += CanObjectEntry;
            scenePermissions.OnObjectEnterWithScripts += OnObjectEnterWithScripts;

            scenePermissions.OnDuplicateObject += CanDuplicateObject;
            scenePermissions.OnDeleteObjectByIDs += CanDeleteObjectByIDs;
            scenePermissions.OnDeleteObject += CanDeleteObject;
            scenePermissions.OnEditObjectByIDs += CanEditObjectByIDs;
            scenePermissions.OnEditObject += CanEditObject;
            scenePermissions.OnEditObjectPerms += CanEditObjectPerms;
            scenePermissions.OnInventoryTransfer += CanInventoryTransfer;
            scenePermissions.OnMoveObject += CanMoveObject;
            scenePermissions.OnTakeObject += CanTakeObject;
            scenePermissions.OnTakeCopyObject += CanTakeCopyObject;
            scenePermissions.OnLinkObject += CanLinkObject;
            scenePermissions.OnDelinkObject += CanDelinkObject;
            scenePermissions.OnDeedObject += CanDeedObject;
            scenePermissions.OnSellGroupObject += CanSellGroupObject;
            scenePermissions.OnSellObjectByUserID += CanSellObjectByUserID;
            scenePermissions.OnSellObject += CanSellObject;
            
            scenePermissions.OnCreateObjectInventory += CanCreateObjectInventory;
            scenePermissions.OnEditObjectInventory += CanEditObjectInventory;
            scenePermissions.OnCopyObjectInventory += CanCopyObjectInventory;
            scenePermissions.OnDeleteObjectInventory += CanDeleteObjectInventory;
            scenePermissions.OnDoObjectInvToObjectInv += CanDoObjectInvToObjectInv;
            scenePermissions.OnDropInObjectInv += CanDropInObjectInv;

            scenePermissions.OnViewNotecard += CanViewNotecard;
            scenePermissions.OnViewScript += CanViewScript;
            scenePermissions.OnEditNotecard += CanEditNotecard;
            scenePermissions.OnEditScript += CanEditScript;
            scenePermissions.OnResetScript += CanResetScript;
            scenePermissions.OnRunScript += CanRunScript;
            scenePermissions.OnCompileScript += CanCompileScript;
            
            scenePermissions.OnCreateUserInventory += CanCreateUserInventory;
            scenePermissions.OnCopyUserInventory += CanCopyUserInventory;
            scenePermissions.OnEditUserInventory += CanEditUserInventory;
            scenePermissions.OnDeleteUserInventory += CanDeleteUserInventory;

            scenePermissions.OnControlPrimMedia += CanControlPrimMedia;
            scenePermissions.OnInteractWithPrimMedia += CanInteractWithPrimMedia;

            m_scene.AddCommand("Users", this, "bypass permissions",
                    "bypass permissions <true / false>",
                    "Bypass permission checks",
                    HandleBypassPermissions);

            m_scene.AddCommand("Users", this, "force permissions",
                    "force permissions <true / false>",
                    "Force permissions on or off",
                    HandleForcePermissions);

            m_scene.AddCommand("Debug", this, "debug permissions",
                    "debug permissions <true / false>",
                    "Turn on permissions debugging",
                    HandleDebugPermissions);

        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene.UnregisterModuleInterface<IPermissionsModule>(this);

            scenePermissions.OnBypassPermissions -= BypassPermissions;
            scenePermissions.OnSetBypassPermissions -= SetBypassPermissions;
            scenePermissions.OnPropagatePermissions -= PropagatePermissions;

            scenePermissions.OnIsGridGod -= IsGridAdministrator;
            scenePermissions.OnIsAdministrator -= IsAdministrator;
            scenePermissions.OnIsEstateManager -= IsEstateManager;

            scenePermissions.OnGenerateClientFlags -= GenerateClientFlags;

            scenePermissions.OnIssueEstateCommand -= CanIssueEstateCommand;
            scenePermissions.OnRunConsoleCommand -= CanRunConsoleCommand;

            scenePermissions.OnTeleport -= CanTeleport;

            scenePermissions.OnInstantMessage -= CanInstantMessage;

            scenePermissions.OnAbandonParcel -= CanAbandonParcel;
            scenePermissions.OnReclaimParcel -= CanReclaimParcel;
            scenePermissions.OnDeedParcel -= CanDeedParcel;
            scenePermissions.OnSellParcel -= CanSellParcel;
            scenePermissions.OnEditParcelProperties -= CanEditParcelProperties;
            scenePermissions.OnTerraformLand -= CanTerraformLand;
            scenePermissions.OnBuyLand -= CanBuyLand;

            scenePermissions.OnRezObject -= CanRezObject;
            scenePermissions.OnObjectEntry -= CanObjectEntry;
            scenePermissions.OnObjectEnterWithScripts -= OnObjectEnterWithScripts;

            scenePermissions.OnReturnObjects -= CanReturnObjects;

            scenePermissions.OnDuplicateObject -= CanDuplicateObject;
            scenePermissions.OnDeleteObjectByIDs -= CanDeleteObjectByIDs;
            scenePermissions.OnDeleteObject -= CanDeleteObject;
            scenePermissions.OnEditObjectByIDs -= CanEditObjectByIDs;
            scenePermissions.OnEditObject -= CanEditObject;
            scenePermissions.OnEditObjectPerms -= CanEditObjectPerms;
            scenePermissions.OnInventoryTransfer -= CanInventoryTransfer;
            scenePermissions.OnMoveObject -= CanMoveObject;
            scenePermissions.OnTakeObject -= CanTakeObject;
            scenePermissions.OnTakeCopyObject -= CanTakeCopyObject;
            scenePermissions.OnLinkObject -= CanLinkObject;
            scenePermissions.OnDelinkObject -= CanDelinkObject;
            scenePermissions.OnDeedObject -= CanDeedObject;

            scenePermissions.OnSellGroupObject -= CanSellGroupObject;
            scenePermissions.OnSellObjectByUserID -= CanSellObjectByUserID;
            scenePermissions.OnSellObject -= CanSellObject;
            
            scenePermissions.OnCreateObjectInventory -= CanCreateObjectInventory;
            scenePermissions.OnEditObjectInventory -= CanEditObjectInventory;
            scenePermissions.OnCopyObjectInventory -= CanCopyObjectInventory;
            scenePermissions.OnDeleteObjectInventory -= CanDeleteObjectInventory;
            scenePermissions.OnDoObjectInvToObjectInv -= CanDoObjectInvToObjectInv;
            scenePermissions.OnDropInObjectInv -= CanDropInObjectInv;

            scenePermissions.OnViewNotecard -= CanViewNotecard;
            scenePermissions.OnViewScript -= CanViewScript;
            scenePermissions.OnEditNotecard -= CanEditNotecard;
            scenePermissions.OnEditScript -= CanEditScript;
            scenePermissions.OnResetScript -= CanResetScript;
            scenePermissions.OnRunScript -= CanRunScript;
            scenePermissions.OnCompileScript -= CanCompileScript;
            
            scenePermissions.OnCreateUserInventory -= CanCreateUserInventory;
            scenePermissions.OnCopyUserInventory -= CanCopyUserInventory;
            scenePermissions.OnEditUserInventory -= CanEditUserInventory;
            scenePermissions.OnDeleteUserInventory -= CanDeleteUserInventory;

            scenePermissions.OnControlPrimMedia -= CanControlPrimMedia;
            scenePermissions.OnInteractWithPrimMedia -= CanInteractWithPrimMedia;

        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "DefaultPermissionsModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region Console command handlers

        public void HandleBypassPermissions(string module, string[] args)
        {
            if (m_scene.ConsoleScene() is not null &&
                m_scene.ConsoleScene() != m_scene)
            {
                return;
            }

            if (args.Length > 2)
            {
                if (!bool.TryParse(args[2], out bool val))
                    return;

                m_bypassPermissions = val;

                m_log.InfoFormat(
                    "[PERMISSIONS]: Set permissions bypass to {0} for {1}",
                    m_bypassPermissions, m_scene.RegionInfo.RegionName);
            }
        }

        public void HandleForcePermissions(string module, string[] args)
        {
            if (m_scene.ConsoleScene() is not null &&
                m_scene.ConsoleScene() != m_scene)
            {
                return;
            }

            if (!m_bypassPermissions)
            {
                m_log.Error("[PERMISSIONS] Permissions can't be forced unless they are bypassed first");
                return;
            }

            if (args.Length > 2)
            {
                if (!bool.TryParse(args[2], out bool val))
                    return;

                m_bypassPermissionsValue = val;

                m_log.InfoFormat("[PERMISSIONS] Forced permissions to {0} in {1}", m_bypassPermissionsValue, m_scene.RegionInfo.RegionName);
            }
        }

        public void HandleDebugPermissions(string module, string[] args)
        {
            if (m_scene.ConsoleScene() is not null &&
                m_scene.ConsoleScene() != m_scene)
            {
                return;
            }

            if (args.Length > 2)
            {
                if (!bool.TryParse(args[2], out bool val))
                    return;

                m_debugPermissions = val;

                m_log.InfoFormat("[PERMISSIONS] Set permissions debugging to {0} in {1}", m_debugPermissions, m_scene.RegionInfo.RegionName);
            }
        }

        #endregion

        #region Helper Functions
        protected void SendPermissionError(UUID user, string reason)
        {
            m_scene.EventManager.TriggerPermissionError(user, reason);
        }

        protected void DebugPermissionInformation(string permissionCalled)
        {
            if (m_debugPermissions)
                m_log.Debug("[PERMISSIONS]: " + permissionCalled + " was called from " + m_scene.RegionInfo.RegionName);
        }

        /// <summary>
        /// Checks if the given group is active and if the user is a group member
        /// with the powers requested (powers = 0 for no powers check)
        /// </summary>
        /// <param name="groupID"></param>
        /// <param name="userID"></param>
        /// <param name="powers"></param>
        /// <returns></returns>
        protected bool IsGroupMember(UUID groupID, UUID userID, ulong powers)
        {
            if (GroupsModule is null)
                return false;

            GroupMembershipData gmd = GroupsModule.GetMembershipData(groupID, userID);

            if (gmd is not null)
            {
                if (((gmd.GroupPowers != 0) && powers == 0) || (gmd.GroupPowers & powers) == powers)
                    return true;
            }

            return false;
        }

        protected bool GroupMemberPowers(UUID groupID, UUID userID, ref ulong powers)
        {
            powers = 0;
            if (GroupsModule is null)
                return false;

            GroupMembershipData gmd = GroupsModule.GetMembershipData(groupID, userID);
            
            if (gmd is not null)
            {
                powers = gmd.GroupPowers;
                return true;
            }
            return false;
        }

        protected bool GroupMemberPowers(UUID groupID, ScenePresence sp, ref ulong powers)
        {
            powers = 0;
            IClientAPI client = sp.ControllingClient;
            if (client is null)
                return false;

            if(!client.IsGroupMember(groupID))
                return false;
            
            powers =  client.GetGroupPowers(groupID);
            return true;
        }

        /// <summary>
        /// Parse a user set configuration setting
        /// </summary>
        /// <param name="config"></param>
        /// <param name="settingName"></param>
        /// <param name="defaultValue">The default value for this attribute</param>
        /// <returns>The parsed value</returns>
        private static UserSet ParseUserSetConfigSetting(IConfigSource config, string settingName, UserSet defaultValue)
        {
            UserSet userSet = defaultValue;

            string rawSetting = Util.GetConfigVarFromSections<string>(config, settingName,
                new string[] {"Startup", "Permissions"}, defaultValue.ToString());

            // Temporary measure to allow 'gods' to be specified in config for consistency's sake.  In the long term
            // this should disappear.
            if ("gods" == rawSetting.ToLower())
                rawSetting = UserSet.Administrators.ToString();

            // Doing it this was so that we can do a case insensitive conversion
            try
            {
                userSet = (UserSet)Enum.Parse(typeof(UserSet), rawSetting, true);
            }
            catch
            {
                m_log.ErrorFormat(
                    "[PERMISSIONS]: {0} is not a valid {1} value, setting to {2}",
                    rawSetting, settingName, userSet);
            }

            m_log.DebugFormat("[PERMISSIONS]: {0} {1}", settingName, userSet);

            return userSet;
        }

        /// <summary>
        /// Is the user regarded as an administrator?
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        protected bool IsAdministrator(UUID user)
        {
            if (user.IsZero())
                return false;

            if (IsGridAdministrator(user))
                return true;

            return false;
        }

        /// <summary>
        /// Is the given user a God throughout the grid (not just in the current scene)?
        /// </summary>
        /// <param name="user">The user</param>
        /// <param name="scene">Unused, can be null</param>
        /// <returns></returns>
        protected bool IsGridAdministrator(UUID user)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (user.IsZero())
                return false;

            if (m_allowGridAdmins)
            {
                ScenePresence sp = m_scene.GetScenePresence(user);
                if (sp is not null)
                    return (sp.GodController.UserLevel >= 200);

                UserAccount account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, user);
                if (account is not null)
                    return (account.UserLevel >= 200);
            }

            return false;
        }

        protected bool IsFriendWithPerms(UUID user, UUID objectOwner)
        {
            if (FriendsModule is null)
                return false;

            if (user.IsZero())
                return false;

            int friendPerms = FriendsModule.GetRightsGrantedByFriend(user, objectOwner);
            return (friendPerms & (int)FriendRights.CanModifyObjects) != 0;
        }

        protected bool IsEstateManager(UUID user)
        {
            if (user.IsZero())
                return false;

            return m_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(user);
        }

#endregion

        public bool PropagatePermissions()
        {
            if (m_bypassPermissions)
                return false;

            return m_propagatePermissions;
        }

        public bool BypassPermissions()
        {
            return m_bypassPermissions;
        }

        public void SetBypassPermissions(bool value)
        {
            m_bypassPermissions=value;
        }

        #region Object Permissions

        const uint DEFAULT_FLAGS  = (uint)(
            PrimFlags.ObjectCopy | // Tells client you can copy the object
            PrimFlags.ObjectModify | // tells client you can modify the object
            PrimFlags.ObjectMove |   // tells client that you can move the object (only, no mod)
            PrimFlags.ObjectTransfer | // tells the client that you can /take/ the object if you don't own it
            PrimFlags.ObjectYouOwner | // Tells client that you're the owner of the object
            PrimFlags.ObjectAnyOwner | // Tells client that someone owns the object
            PrimFlags.ObjectOwnerModify // Tells client that you're the owner of the object
            );

        const uint NOT_DEFAULT_FLAGS  = (uint)~(
            PrimFlags.ObjectCopy | // Tells client you can copy the object
            PrimFlags.ObjectModify | // tells client you can modify the object
            PrimFlags.ObjectMove |   // tells client that you can move the object (only, no mod)
            PrimFlags.ObjectTransfer | // tells the client that you can /take/ the object if you don't own it
            PrimFlags.ObjectYouOwner | // Tells client that you're the owner of the object
            PrimFlags.ObjectAnyOwner | // Tells client that someone owns the object
            PrimFlags.ObjectOwnerModify // Tells client that you're the owner of the object
            );

        const uint EXTRAOWNERMASK = (uint)(
                PrimFlags.ObjectYouOwner | 
                PrimFlags.ObjectAnyOwner
                );

        const uint EXTRAGODMASK = (uint)(
                PrimFlags.ObjectYouOwner | 
                PrimFlags.ObjectAnyOwner |
                PrimFlags.ObjectOwnerModify |
                PrimFlags.ObjectModify |
                PrimFlags.ObjectMove
                );

        const uint GOD_FLAGS  = (uint)(
            PrimFlags.ObjectCopy | // Tells client you can copy the object
            PrimFlags.ObjectModify | // tells client you can modify the object
            PrimFlags.ObjectMove |   // tells client that you can move the object (only, no mod)
            PrimFlags.ObjectTransfer | // tells the client that you can /take/ the object if you don't own it
            PrimFlags.ObjectYouOwner | // Tells client that you're the owner of the object
            PrimFlags.ObjectAnyOwner | // Tells client that someone owns the object
            PrimFlags.ObjectOwnerModify // Tells client that you're the owner of the object
            );

        const uint LOCKED_GOD_FLAGS  = (uint)(
            PrimFlags.ObjectCopy | // Tells client you can copy the object
            PrimFlags.ObjectTransfer | // tells the client that you can /take/ the object if you don't own it
            PrimFlags.ObjectYouOwner | // Tells client that you're the owner of the object
            PrimFlags.ObjectAnyOwner // Tells client that someone owns the object
            );

        const uint SHAREDMASK  = (uint)(
            PermissionMask.Move |
            PermissionMask.Modify |
            PermissionMask.Copy
            );

        public uint GenerateClientFlags(SceneObjectPart task, ScenePresence sp, uint curEffectivePerms)
        {
            if(sp is null  || task is null || curEffectivePerms == 0)
                return 0;

            // Remove any of the objectFlags that are temporary.  These will get added back if appropriate
            uint objflags = curEffectivePerms & NOT_DEFAULT_FLAGS ;

            uint returnMask;

            SceneObjectGroup grp = task.ParentGroup;
            if(grp is null)
                return 0;

            bool unlocked = (grp.RootPart.OwnerMask & (uint)PermissionMask.Move) != 0;

            if(sp.IsGod)
            {
                // do locked on objects owned by admin
                if(!unlocked && sp.UUID.Equals(task.OwnerID))
                    return objflags | LOCKED_GOD_FLAGS;
                else
                    return objflags | GOD_FLAGS;
            }

            //bypass option == owner rights
            if (m_bypassPermissions)
            {
                returnMask = ApplyObjectModifyMasks(task.OwnerMask, objflags, true);  //??
                returnMask |= EXTRAOWNERMASK;
                if((returnMask & (uint)PrimFlags.ObjectModify) != 0)
                    returnMask |= (uint)PrimFlags.ObjectOwnerModify;
                return returnMask;
            }

            uint grpEffectiveOwnerPerms = grp.EffectiveOwnerPerms;
            // owner
            if (sp.UUID.Equals(task.OwnerID))
            {
                returnMask = ApplyObjectModifyMasks(grpEffectiveOwnerPerms, objflags, unlocked);
                returnMask |= EXTRAOWNERMASK;
                if((returnMask & (uint)PrimFlags.ObjectModify) != 0)
                    returnMask |= (uint)PrimFlags.ObjectOwnerModify;
                return returnMask;
            }

            // if not god or owner, do attachments as everyone
            if (task.ParentGroup.IsAttachment)
            {
                returnMask = ApplyObjectModifyMasks(grp.EffectiveEveryOnePerms, objflags, unlocked);
                if (!task.OwnerID.IsZero())
                    returnMask |= (uint)PrimFlags.ObjectAnyOwner;
                return returnMask;
            }

            bool notGroupdOwned = task.OwnerID.NotEqual(task.GroupID);

            if ((grpEffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                grpEffectiveOwnerPerms &= ~(uint)PermissionMask.Copy;

            // if friends with rights then owner
            if (notGroupdOwned && IsFriendWithPerms(sp.UUID, task.OwnerID))
            {
                returnMask = ApplyObjectModifyMasks(grpEffectiveOwnerPerms, objflags, unlocked);
                returnMask |= EXTRAOWNERMASK;
                if((returnMask & (uint)PrimFlags.ObjectModify) != 0)
                    returnMask |= (uint)PrimFlags.ObjectOwnerModify;
                return returnMask;
            }

            // group owned or shared ?
            ulong  powers = 0;
            if(task.GroupID.IsNotZero() && GroupMemberPowers(task.GroupID, sp, ref powers))
            {
                if(notGroupdOwned)
                {
                    // group sharing or everyone
                    returnMask = ApplyObjectModifyMasks(grp.EffectiveGroupOrEveryOnePerms, objflags, unlocked);
                    if (task.OwnerID.IsNotZero())
                        returnMask |= (uint)PrimFlags.ObjectAnyOwner;
                    return returnMask;
                }

                // object is owned by group, check role powers
                if((powers & (ulong)GroupPowers.ObjectManipulate) == 0)
                {
                    // group sharing or everyone
                    returnMask = ApplyObjectModifyMasks(grp.EffectiveGroupOrEveryOnePerms, objflags, unlocked);
                    returnMask |=
                        (uint)PrimFlags.ObjectGroupOwned |
                        (uint)PrimFlags.ObjectAnyOwner;
                    return returnMask;
                }

                returnMask = ApplyObjectModifyMasks(grpEffectiveOwnerPerms, objflags, unlocked);
                returnMask |= 
                    (uint)PrimFlags.ObjectGroupOwned |
                    (uint)PrimFlags.ObjectYouOwner |
                    (uint)PrimFlags.ObjectAnyOwner;
                if ((returnMask & (uint)PrimFlags.ObjectModify) != 0)
                    returnMask |= (uint)PrimFlags.ObjectOwnerModify;
                return returnMask;
            }

            // fallback is everyone rights
            returnMask = ApplyObjectModifyMasks(grp.EffectiveEveryOnePerms, objflags, unlocked);
            if (task.OwnerID.IsNotZero())
                returnMask |= (uint)PrimFlags.ObjectAnyOwner;
            return returnMask;
        }

        private uint ApplyObjectModifyMasks(uint setPermissionMask, uint objectFlagsMask, bool unlocked)
        {
            // We are adding the temporary objectflags to the object's objectflags based on the
            // permission flag given.  These change the F flags on the client.

            if ((setPermissionMask & (uint)PermissionMask.Copy) != 0)
            {
                objectFlagsMask |= (uint)PrimFlags.ObjectCopy;
            }

            if (unlocked)
            {
                if ((setPermissionMask & (uint)PermissionMask.Move) != 0)
                {
                    objectFlagsMask |= (uint)PrimFlags.ObjectMove;
                }

                if ((setPermissionMask & (uint)PermissionMask.Modify) != 0)
                {
                    objectFlagsMask |= (uint)PrimFlags.ObjectModify;
                }
            }

            if ((setPermissionMask & (uint)PermissionMask.Transfer) != 0)
            {
                objectFlagsMask |= (uint)PrimFlags.ObjectTransfer;
            }

            return objectFlagsMask;
        }

        // OARs still need this method that handles offline users
        public PermissionClass GetPermissionClass(UUID user, SceneObjectPart obj)
        {
            if (obj is null)
                return PermissionClass.Everyone;

            if (m_bypassPermissions)
                return PermissionClass.Owner;

            // Object owners should be able to edit their own content
            if (user.Equals(obj.OwnerID))
                return PermissionClass.Owner;

            // Admin should be able to edit anything in the sim (including admin objects)
            if (IsAdministrator(user))
                return PermissionClass.Owner;

            if(!obj.ParentGroup.IsAttachment)
            {
                if (IsFriendWithPerms(user, obj.OwnerID) )
                    return PermissionClass.Owner;

                // Group permissions
                if (obj.GroupID.IsNotZero() && IsGroupMember(obj.GroupID, user, 0))
                    return PermissionClass.Group;
            }

            return PermissionClass.Everyone;
        }

        // get effective object permissions using user UUID. User rights will be fixed
        protected uint GetObjectPermissions(UUID currentUser, SceneObjectGroup group, bool denyOnLocked)
        {
            if (group is null)
                return 0;

            SceneObjectPart root = group.RootPart;
            if (root is null)
                return 0;

            bool locked = denyOnLocked && ((root.OwnerMask & (uint)PermissionMask.Move) == 0);

            if (IsAdministrator(currentUser))
            {
                // do lock on admin owned objects
                if(locked && currentUser.Equals(group.OwnerID))
                    return (uint)(PermissionMask.AllEffective & ~(PermissionMask.Modify | PermissionMask.Move));
                return (uint)PermissionMask.AllEffective;
            }

            uint lockmask = (uint)PermissionMask.AllEffective;
            if(locked)
                lockmask &= ~(uint)(PermissionMask.Modify | PermissionMask.Move);

            uint grpEffectiveOwnerPerms = group.EffectiveOwnerPerms & lockmask;

            if (currentUser.Equals(group.OwnerID))
                return grpEffectiveOwnerPerms & lockmask;
            
            if (group.IsAttachment)
                return 0;

            if ((grpEffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                grpEffectiveOwnerPerms &= ~(uint)PermissionMask.Copy;

            bool notgroudOwned = group.GroupID.NotEqual(group.OwnerID);

            if (notgroudOwned && IsFriendWithPerms(currentUser, group.OwnerID))
                return grpEffectiveOwnerPerms & lockmask;

            ulong powers = 0;
            if (group.GroupID.IsNotZero() && GroupMemberPowers(group.GroupID, currentUser, ref powers))
            {
                if(notgroudOwned)
                    return  group.EffectiveGroupOrEveryOnePerms & lockmask;

                if((powers & (ulong)GroupPowers.ObjectManipulate) == 0)
                    return  group.EffectiveGroupOrEveryOnePerms & lockmask;

                return grpEffectiveOwnerPerms & lockmask;
            }

            return group.EffectiveEveryOnePerms & lockmask;
        }

        // get effective object permissions using present presence. So some may depend on requested rights (ie God)
        protected uint GetObjectPermissions(ScenePresence sp, SceneObjectGroup group, bool denyOnLocked)
        {
            if (sp is null || sp.IsDeleted || group is null || group.IsDeleted)
                return 0;

            SceneObjectPart root = group.RootPart;
            if (root is null)
                return 0;

            bool locked = denyOnLocked && ((root.OwnerMask & (uint)PermissionMask.Move) == 0);

            if (sp.IsGod)
            {
                if(locked && sp.UUID.Equals(group.OwnerID))
                    return (uint)(PermissionMask.AllEffective & ~(PermissionMask.Modify | PermissionMask.Move));
                return (uint)PermissionMask.AllEffective;
            }

            uint lockmask = (uint)PermissionMask.AllEffective;
            if(locked)
                lockmask &= ~(uint)(PermissionMask.Modify | PermissionMask.Move);

            uint ownerperms = group.EffectiveOwnerPerms;
            if (sp.UUID.Equals(group.OwnerID))
                return ownerperms & lockmask;
            
            if (group.IsAttachment)
                return 0;

            if ((ownerperms & (uint)PermissionMask.Transfer) == 0)
                ownerperms &= ~(uint)PermissionMask.Copy;

            bool notgroudOwned = group.GroupID.NotEqual(group.OwnerID);

            if (notgroudOwned && IsFriendWithPerms(sp.UUID, group.OwnerID))
            {
                return ownerperms & lockmask;
            }

            ulong powers = 0;
            if (group.GroupID.IsNotZero() && GroupMemberPowers(group.GroupID, sp, ref powers))
            {
                if(notgroudOwned)
                    return  group.EffectiveGroupOrEveryOnePerms & lockmask;

                if((powers & (ulong)GroupPowers.ObjectManipulate) == 0)
                    return  group.EffectiveGroupOrEveryOnePerms & lockmask;

                return ownerperms & lockmask;
            }

            return group.EffectiveEveryOnePerms & lockmask;
        }

        private uint GetObjectItemPermissions(UUID userID, TaskInventoryItem ti)
        {
            if(ti.OwnerID.Equals(userID))
                return ti.CurrentPermissions;
            
            if(IsAdministrator(userID))
                return (uint)PermissionMask.AllEffective;
            // ??
            if (IsFriendWithPerms(userID, ti.OwnerID))
                return ti.CurrentPermissions;

            if(ti.GroupID.IsNotZero())
            {
                ulong powers = 0;
                if(GroupMemberPowers(ti.GroupID, userID, ref powers))
                {
                    if(ti.GroupID.Equals(ti.OwnerID))
                    {
                        if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                            return ti.CurrentPermissions;
                    }
                    return ti.GroupPermissions;
                } 
            }

            return 0;
        }

        private uint GetObjectItemPermissions(ScenePresence sp, TaskInventoryItem ti, bool notEveryone)
        {
            if(ti.OwnerID.Equals(sp.UUID))
                return ti.CurrentPermissions;
 
            // ??
            if (IsFriendWithPerms(sp.UUID, ti.OwnerID))
                return ti.CurrentPermissions;

            if(ti.GroupID.IsNotZero())
            {
                ulong powers = 0;
                if(GroupMemberPowers(ti.GroupID, sp.UUID, ref powers))
                {
                    if(ti.GroupID.Equals(ti.OwnerID))
                    {
                        if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                            return ti.CurrentPermissions;
                    }
                    uint p = ti.GroupPermissions;
                    if(!notEveryone)
                        p |= ti.EveryonePermissions;
                    return p;
                } 
            }

            if(notEveryone)
                return 0;

            return ti.EveryonePermissions;
        }
        #endregion

        #region Generic Permissions
        /* this still does nothing but waste time
        protected bool GenericCommunicationPermission(UUID user, UUID target)
        {
            // Setting this to true so that cool stuff can happen until we define what determines Generic Communication Permission
            bool permission = true;
            string reason = "Only registered users may communicate with another account.";

            // Uhh, we need to finish this before we enable it..   because it's blocking all sorts of goodies and features
            if (IsAdministrator(user))
                permission = true;

            if (IsEstateManager(user))
                permission = true;

            if (!permission)
                SendPermissionError(user, reason);

            return permission;
        }
        */

        public bool GenericEstatePermission(UUID user)
        {
            // Estate admins should be able to use estate tools
            if (IsEstateManager(user))
                return true;

            // Administrators always have permission
            if (IsAdministrator(user))
                return true;

            return false;
        }

        protected bool GenericParcelOwnerPermission(UUID user, ILandObject parcel, ulong groupPowers, bool allowEstateManager)
        {
            if (parcel.LandData.OwnerID.Equals(user))
                return true;

            if (parcel.LandData.IsGroupOwned && IsGroupMember(parcel.LandData.GroupID, user, groupPowers))
                return true;

            if (allowEstateManager && IsEstateManager(user))
                return true;

            if (IsAdministrator(user))
                return true;

            return false;
        }
#endregion

        #region Permission Checks
        private bool CanAbandonParcel(UUID user, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return GenericParcelOwnerPermission(user, parcel, (ulong)GroupPowers.LandRelease, false);
        }

        private bool CanReclaimParcel(UUID user, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return GenericParcelOwnerPermission(user, parcel, 0,true);
        }

        private bool CanDeedParcel(UUID user, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if(parcel.LandData.GroupID.IsZero())
                return false;

            if (IsAdministrator(user))
                return true;

            if (parcel.LandData.OwnerID.NotEqual(user)) // Only the owner can deed!
                return false;

            ScenePresence sp = m_scene.GetScenePresence(user);
            if(sp is null)
                return false;

            IClientAPI client = sp.ControllingClient;
            if ((client.GetGroupPowers(parcel.LandData.GroupID) & (ulong)GroupPowers.LandDeed) == 0)
                return false;

            return true;
        }

        private bool CanDeedObject(ScenePresence sp, SceneObjectGroup sog, UUID targetGroupID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if(sog is null || sog.IsDeleted || sp is null || sp.IsDeleted || targetGroupID.IsZero())
                return false;

            // object has group already?
            if(sog.GroupID.NotEqual(targetGroupID))
                return false;

            // is effectivelly shared?            
            if(sog.EffectiveGroupPerms == 0)
                return false;

            if(sp.IsGod)
                return true;

            // owned by requester?
            if(sog.OwnerID.NotEqual(sp.UUID))
                return false;

            // owner can transfer?
            if((sog.EffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                return false;
            
            // group member ? 
            ulong powers = 0;
            if(!GroupMemberPowers(targetGroupID, sp, ref powers))
                return false;

            // has group rights?
            if ((powers & (ulong)GroupPowers.DeedObject) == 0)
                return false;

            return true;
        }

        private bool CanDuplicateObject(SceneObjectGroup sog, ScenePresence sp)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            uint perms = GetObjectPermissions(sp, sog, false);
            if((perms & (uint)PermissionMask.Copy) == 0)
                return false;

            if(sog.OwnerID.NotEqual(sp.UUID) && (perms & (uint)PermissionMask.Transfer) == 0)
                return false;

            //If they can rez, they can duplicate
            return CanRezObject(0, sp.UUID, sog.AbsolutePosition);
        }

        private bool CanDeleteObject(SceneObjectGroup sog, ScenePresence sp)
        {
            // ignoring locked. viewers should warn and ask for confirmation

            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            if(sog.IsAttachment)
                return false;

            if(sog.OwnerID.Equals(sp.UUID))
                return true;

            if (sp.IsGod)
                return true;

            if (IsFriendWithPerms(sog.UUID, sog.OwnerID))
                return true;

            if (sog.GroupID.IsNotZero())
            {
                ulong powers = 0;
                if(GroupMemberPowers(sog.GroupID, sp, ref powers))
                {
                    if(sog.GroupID.Equals(sog.OwnerID))
                    {
                        if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                            return true;
                    }
                    return  (sog.EffectiveGroupPerms & (uint)PermissionMask.Modify) != 0;
                } 
            }
            return false;
        }

        private bool CanDeleteObjectByIDs(UUID objectID, UUID userID)
        {
            // ignoring locked. viewers should warn and ask for confirmation

            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            if(sog.IsAttachment)
                return false;

            if(sog.OwnerID.Equals(userID))
                return true;

            if (IsAdministrator(userID))
                return true;

            if (IsFriendWithPerms(objectID, sog.OwnerID))
                return true;

            if (sog.GroupID.IsNotZero())
            {
                ulong powers = 0;
                if(GroupMemberPowers(sog.GroupID, userID, ref powers))
                {
                    if(sog.GroupID.Equals(sog.OwnerID))
                    {
                        if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                            return true;
                    }
                    return  (sog.EffectiveGroupPerms & (uint)PermissionMask.Modify) != 0;
                } 
            }
            return false;
        }

        private bool CanEditObjectByIDs(UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanEditObject(SceneObjectGroup sog, ScenePresence sp)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if(sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            uint perms = GetObjectPermissions(sp, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanEditObjectPerms(SceneObjectGroup sog, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null)
                return false;

            if(sog.OwnerID.Equals(userID) || IsAdministrator(userID))
                return true;

            if(sog.GroupID.IsZero() || sog.GroupID.NotEqual(sog.OwnerID))
                return false;

            uint perms = sog.EffectiveOwnerPerms;
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;

            ulong powers = 0;
            if(GroupMemberPowers(sog.GroupID, userID, ref powers))
            {
                if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                    return true;
            }

            return false;
        }

        private bool CanEditObjectInventory(UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanEditParcelProperties(UUID userID, ILandObject parcel, GroupPowers p, bool allowManager)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return GenericParcelOwnerPermission(userID, parcel, (ulong)p, false);
        }

        /// <summary>
        /// Check whether the specified user can edit the given script
        /// </summary>
        /// <param name="script"></param>
        /// <param name="objectID"></param>
        /// <param name="user"></param>
        /// <param name="scene"></param>
        /// <returns></returns>
        private bool CanEditScript(UUID script, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (m_allowedScriptEditors == UserSet.Administrators && !IsAdministrator(userID))
                return false;

            // Ordinarily, if you can view it, you can edit it
            // There is no viewing a no mod script
            //
            return CanViewScript(script, objectID, userID);
        }

        /// <summary>
        /// Check whether the specified user can edit the given notecard
        /// </summary>
        /// <param name="notecard"></param>
        /// <param name="objectID"></param>
        /// <param name="user"></param>
        /// <param name="scene"></param>
        /// <returns></returns>
        private bool CanEditNotecard(UUID notecard, UUID objectID, UUID user)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (objectID.IsZero()) // User inventory
            {
                IInventoryService invService = m_scene.InventoryService;
                InventoryItemBase assetRequestItem = invService.GetItem(user, notecard);
                if (assetRequestItem is null && LibraryRootFolder is not null) // Library item
                {
                    assetRequestItem = LibraryRootFolder.FindItem(notecard);

                    if (assetRequestItem is not null) // Implicitly readable
                        return true;
                }

                // Notecards must be both mod and copy to be saveable
                // This is because of they're not copy, you can't read
                // them, and if they're not mod, well, then they're
                // not mod. Duh.
                //
                if ((assetRequestItem.CurrentPermissions &
                        ((uint)PermissionMask.Modify |
                        (uint)PermissionMask.Copy)) !=
                        ((uint)PermissionMask.Modify |
                        (uint)PermissionMask.Copy))
                    return false;
            }
            else // Prim inventory
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
                if (part is null)
                    return false;

                SceneObjectGroup sog = part.ParentGroup;
                if (sog is null)
                    return false;

                // check object mod right
                uint perms = GetObjectPermissions(user, sog, true);
                if((perms & (uint)PermissionMask.Modify) == 0)
                    return false;

                TaskInventoryItem ti = part.Inventory.GetInventoryItem(notecard);
                if (ti is null)
                    return false;
               
                if (ti.OwnerID.NotEqual(user))
                {
                    if (ti.GroupID.IsZero())
                        return false;

                    ulong powers = 0;
                    if(!GroupMemberPowers(ti.GroupID, user, ref powers))
                        return false;

                    if(ti.GroupID.Equals(ti.OwnerID) && (powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                    {
                        if ((ti.CurrentPermissions & ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy)) ==
                        ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy))
                            return true;
                    }
                    if ((ti.GroupPermissions & ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy)) ==
                        ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy))
                        return true;
                    return false;
                }

                // Require full perms
                if ((ti.CurrentPermissions & ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy)) !=
                        ((uint)PermissionMask.Modify | (uint)PermissionMask.Copy))
                    return false;
            }
            return true;
        }

        private bool CanInstantMessage(UUID user, UUID target)
        {
            return true; // we still did not define this
            /*
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            // If the sender is an object, check owner instead
            //
            SceneObjectPart part = m_scene.GetSceneObjectPart(user);
            if (part != null)
                user = part.OwnerID;

            return GenericCommunicationPermission(user, target);
            */
        }

        private bool CanInventoryTransfer(UUID user, UUID target)
        {
            return true; // we still did not define this
            /*
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return GenericCommunicationPermission(user, target);
            */
        }

        private bool CanIssueEstateCommand(UUID user, bool ownerCommand)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (IsAdministrator(user))
                return true;

            if (ownerCommand)
                return m_scene.RegionInfo.EstateSettings.IsEstateOwner(user);

            return IsEstateManager(user);
        }

        private bool CanMoveObject(SceneObjectGroup sog, ScenePresence sp)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);

            if(sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            if (m_bypassPermissions)
            {
                if (sog.OwnerID.NotEqual(sp.UUID) && sog.IsAttachment)
                    return false;
                return m_bypassPermissionsValue;
            }

            uint perms = GetObjectPermissions(sp, sog, true);
            if((perms & (uint)PermissionMask.Move) == 0)
                return false;
            return true;
        }

        private bool CanObjectEntry(SceneObjectGroup sog, bool enteringRegion, Vector3 newPoint)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);

            float newX = newPoint.X;
            float newY = newPoint.Y;

            // allow outside region this is needed for crossings
            if (newX < -1f || newX > (m_scene.RegionInfo.RegionSizeX + 1.0f) ||
                newY < -1f || newY > (m_scene.RegionInfo.RegionSizeY + 1.0f) )
                return true;

            if(sog is null || sog.IsDeleted)
                return false;

            if (m_bypassPermissions)
                return m_bypassPermissionsValue;

            ILandObject parcel = m_scene.LandChannel.GetLandObject(newX, newY);
            if (parcel is null)
                return false;

            if ((parcel.LandData.Flags & ((int)ParcelFlags.AllowAPrimitiveEntry)) != 0)
                return true;

            if (!enteringRegion)
            {
                Vector3 oldPoint = sog.AbsolutePosition;
                ILandObject fromparcel = m_scene.LandChannel.GetLandObject(oldPoint.X, oldPoint.Y);
                if (fromparcel is not null && fromparcel.Equals(parcel)) // it already entered parcel ????
                    return true;
            }

            UUID userID = sog.OwnerID;
            LandData landdata = parcel.LandData;

            if (landdata.OwnerID.Equals(userID))
                return true;

            if (IsAdministrator(userID))
                return true;

            if (landdata.GroupID.IsNotZero())
            {
                if ((parcel.LandData.Flags & ((int)ParcelFlags.AllowGroupObjectEntry)) != 0)
                    return IsGroupMember(landdata.GroupID, userID, 0);

                 if (landdata.IsGroupOwned && IsGroupMember(landdata.GroupID, userID, (ulong)GroupPowers.AllowRez))
                    return true;
            }

            //Otherwise, false!
            return false;
        }

        private bool OnObjectEnterWithScripts(SceneObjectGroup sog, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);

            if(sog is null || sog.IsDeleted)
                return false;

            if (m_bypassPermissions)
                return m_bypassPermissionsValue;

            if (parcel is null)
                return true;

            int checkflags = ((int)ParcelFlags.AllowAPrimitiveEntry);
            bool scripts = (sog.ScriptCount() > 0);
            if(scripts)
                checkflags |= ((int)ParcelFlags.AllowOtherScripts);

            if ((parcel.LandData.Flags & checkflags) == checkflags)
                return true;

            UUID userID = sog.OwnerID;
            LandData landdata = parcel.LandData;

            if (landdata.OwnerID.Equals(userID))
                return true;

            if (IsAdministrator(userID))
                return true;

            if (landdata.GroupID.IsNotZero())
            {
                checkflags = (int)ParcelFlags.AllowGroupObjectEntry;
                if(scripts)
                    checkflags |= ((int)ParcelFlags.AllowGroupScripts);

                if ((parcel.LandData.Flags & checkflags) == checkflags)
                    return IsGroupMember(landdata.GroupID, userID, 0);

                 if (landdata.IsGroupOwned && IsGroupMember(landdata.GroupID, userID, (ulong)GroupPowers.AllowRez))
                    return true;
            }

            //Otherwise, false!
            return false;
        }

        private bool CanReturnObjects(ILandObject land, ScenePresence sp, List<SceneObjectGroup> objects)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if(sp is null)
                return true;  // assuming that in this case rights are as owner

            bool isPrivUser = sp.IsGod || IsEstateManager(sp.UUID);

            IClientAPI client = sp.ControllingClient;

            ulong powers;
            ILandObject l;

            foreach (SceneObjectGroup g in new List<SceneObjectGroup>(objects))
            {
                if(g.IsAttachment)
                {
                    objects.Remove(g);
                    continue;
                }

                if (isPrivUser || g.OwnerID.Equals(sp.UUID))
                    continue;

                // This is a short cut for efficiency. If land is non-null,
                // then all objects are on that parcel and we can save
                // ourselves the checking for each prim. Much faster.
                //
                if (land is not null)
                {
                    l = land;
                }
                else
                {
                    Vector3 pos = g.AbsolutePosition;
                    l = m_scene.LandChannel.GetLandObject(pos.X, pos.Y);
                }

                // If it's not over any land, then we can't do a thing
                if (l is null || l.LandData is null)
                {
                    objects.Remove(g);
                    continue;
                }

                LandData ldata = l.LandData;
                // If we own the land outright, then allow
                //
                if (ldata.OwnerID.Equals(sp.UUID))
                    continue;

                // Group voodoo
                //
                if (ldata.IsGroupOwned)
                {
                    // Not a group member, or no rights at all
                    //
                    powers = client.GetGroupPowers(ldata.GroupID);
                    if(powers == 0)
                    {
                        objects.Remove(g);
                        continue;
                    }
 
                    // Group deeded object?
                    //
                    if (g.OwnerID.Equals(ldata.GroupID) && (powers & (ulong)GroupPowers.ReturnGroupOwned) == 0)
                    {
                        objects.Remove(g);
                        continue;
                    }

                    // Group set object?
                    //
                    if (g.GroupID.Equals(ldata.GroupID) && (powers & (ulong)GroupPowers.ReturnGroupSet) == 0)
                    {
                        objects.Remove(g);
                        continue;
                    }

                    if ((powers & (ulong)GroupPowers.ReturnNonGroup) == 0)
                    {
                        objects.Remove(g);
                        continue;
                    }

                    // So we can remove all objects from this group land.
                    // Fine.
                    //
                    continue;
                }

                // By default, we can't remove
                //
                objects.Remove(g);
            }

            if (objects.Count == 0)
                return false;

            return true;
        }

        private bool CanRezObject(int objectCount, UUID userID, Vector3 objectPosition)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions)
                return m_bypassPermissionsValue;

//            m_log.DebugFormat("[PERMISSIONS MODULE]: Checking rez object at {0} in {1}", objectPosition, m_scene.Name);

            ILandObject parcel = m_scene.LandChannel.GetLandObject(objectPosition.X, objectPosition.Y);
            if (parcel is null || parcel.LandData is null)
                return false;

            LandData landdata = parcel.LandData;
            if (userID.Equals(landdata.OwnerID))
                return true;

            if ((landdata.Flags & (uint)ParcelFlags.CreateObjects) != 0)
                return true;

            if(IsAdministrator(userID))
                return true;

            if(landdata.GroupID.IsNotZero())
            {
                if ((landdata.Flags & (uint)ParcelFlags.CreateGroupObjects) != 0)
                    return IsGroupMember(landdata.GroupID, userID, 0);

                if (landdata.IsGroupOwned && IsGroupMember(landdata.GroupID, userID, (ulong)GroupPowers.AllowRez))
                    return true;
            }

            return false;
        }

        private bool CanRunConsoleCommand(UUID user)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;


            return IsAdministrator(user);
        }

        private bool CanRunScript(TaskInventoryItem scriptitem, SceneObjectPart part)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if(scriptitem is null || part is null)
                return false;

            SceneObjectGroup sog = part.ParentGroup;
            if(sog is null)
                return false;

            Vector3 pos = sog.AbsolutePosition;
            ILandObject parcel = m_scene.LandChannel.GetLandObjectClippedXY(pos.X, pos.Y);
            if (parcel is null)
                return false;

            LandData ldata = parcel.LandData;
            if(ldata is null)
                return false;

            uint lflags = ldata.Flags;
 
            if ((lflags & (uint)ParcelFlags.AllowOtherScripts) != 0)
               return true;

            if ((part.OwnerID == ldata.OwnerID))
                return true;

            if (((lflags & (uint)ParcelFlags.AllowGroupScripts) != 0)
                    && ldata.GroupID.IsNotZero() && ldata.GroupID.Equals(part.GroupID))
                return true;
            
            return GenericEstatePermission(part.OwnerID);
        }

        private bool CanSellParcel(UUID user, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return GenericParcelOwnerPermission(user, parcel, (ulong)GroupPowers.LandSetSale, true);
        }

        private bool CanSellGroupObject(UUID userID, UUID groupID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return IsGroupMember(groupID, userID, (ulong)GroupPowers.ObjectSetForSale);
        }

        private bool CanSellObjectByUserID(SceneObjectGroup sog, UUID userID, byte saleType)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || userID.IsZero())
                return false;

            // sell is not a attachment op
            if(sog.IsAttachment)
                return false;

            if(IsAdministrator(userID))
                return true;

            uint sogEffectiveOwnerPerms = sog.EffectiveOwnerPerms;
            if((sogEffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                return false;

            if(saleType == (byte)SaleType.Copy &&
                    (sogEffectiveOwnerPerms & (uint)PermissionMask.Copy) == 0)
                return false;

            if(sog.OwnerID.Equals(userID))
                return true;

            // else only group owned can be sold by members with powers
            if(sog.GroupID.IsZero() || sog.OwnerID.NotEqual(sog.GroupID))
                return false;

            return IsGroupMember(sog.GroupID, userID, (ulong)GroupPowers.ObjectSetForSale);
        }

        private bool CanSellObject(SceneObjectGroup sog, ScenePresence sp, byte saleType)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            // sell is not a attachment op
            if(sog.IsAttachment)
                return false;

            if(sp.IsGod)
                return true;

            uint sogEffectiveOwnerPerms = sog.EffectiveOwnerPerms;
            if((sogEffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                return false;

            if(saleType == (byte)SaleType.Copy &&
                    (sogEffectiveOwnerPerms & (uint)PermissionMask.Copy) == 0)
                return false;

            if(sog.OwnerID.Equals(sp.UUID))
                return true;

            // else only group owned can be sold by members with powers
            if(sog.GroupID.IsZero() || sog.OwnerID.NotEqual(sog.GroupID))
                return false;

            ulong powers = 0;
            if(!GroupMemberPowers(sog.GroupID, sp, ref powers))
                return false;

            if((powers & (ulong)GroupPowers.ObjectSetForSale) == 0)
                return false;

            return true;
        }

        private bool CanTakeObject(SceneObjectGroup sog, ScenePresence sp)
        {
            // ignore locked, viewers shell ask for confirmation
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            // take is not a attachment op
            if(sog.IsAttachment)
                return false;

            if(sog.OwnerID.Equals(sp.UUID))
                return true;

            if (sp.IsGod)
                return true;

            if((sog.EffectiveOwnerPerms & (uint)PermissionMask.Transfer) == 0)
                return false;
 
            if (IsFriendWithPerms(sog.UUID, sog.OwnerID))
                return true;

            if (sog.GroupID.IsNotZero())
            {
                ulong powers = 0;
                if(GroupMemberPowers(sog.GroupID, sp, ref powers))
                {
                    if(sog.GroupID.Equals(sog.OwnerID))
                    {
                        if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                            return true;
                    }
                    return (sog.EffectiveGroupPerms & (uint)PermissionMask.Modify) != 0;
                } 
            }
            return false;
        }

        private bool CanTakeCopyObject(SceneObjectGroup sog, ScenePresence sp)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if (sog is null || sog.IsDeleted || sp is null || sp.IsDeleted)
                return false;

            // refuse on attachments
            if(sog.IsAttachment && !sp.IsGod)
                return false;

            uint perms = GetObjectPermissions(sp, sog, true);
            if((perms & (uint)PermissionMask.Copy) == 0)
            {
                //sp.ControllingClient.SendAgentAlertMessage("Copying this item has been denied by the permissions system", false);
                return false;
            }

            if(sog.OwnerID.NotEqual(sp.UUID) && (perms & (uint)PermissionMask.Transfer) == 0)
                 return false;

            if (sog.OwnerID.NotEqual(sp.UUID) && !IsFriendWithPerms(sp.UUID, sog.OwnerID) && !sp.IsGod)
            {
                if (m_takeCopyRestricted)
                    return false;
            }
            return true;
        }

        private bool CanTerraformLand(UUID userID, Vector3 position)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            // Estate override
            if (GenericEstatePermission(userID))
                return true;

            float X = position.X;
            float Y = position.Y;
            int id = (int)position.Z;
            ILandObject parcel;

            if(id >= 0 && X < 0 && Y < 0)
                parcel = m_scene.LandChannel.GetLandObject(id);
            else
            {
                parcel = m_scene.LandChannel.GetLandObjectClippedXY(X, Y);
            }

            if (parcel is null)
                return false;

            LandData landdata = parcel.LandData;
            if (landdata is null)
                return false;
            
            if ((landdata.Flags & ((int)ParcelFlags.AllowTerraform)) != 0)
                return true;

            if(landdata.OwnerID.Equals(userID))
                return true;
            
            if (landdata.IsGroupOwned && parcel.LandData.GroupID.IsNotZero() &&  
                    IsGroupMember(landdata.GroupID, userID, (ulong)GroupPowers.AllowEditLand))
                return true;

            return false;
        }

        /// <summary>
        /// Check whether the specified user can view the given script
        /// </summary>
        /// <param name="script"></param>
        /// <param name="objectID"></param>
        /// <param name="user"></param>
        /// <param name="scene"></param>
        /// <returns></returns>
        private bool CanViewScript(UUID script, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            // A god is a god is a god
            if (IsAdministrator(userID))
                return true;

            if (objectID.IsZero()) // User inventory
            {
                IInventoryService invService = m_scene.InventoryService;
                InventoryItemBase assetRequestItem = invService.GetItem(userID, script);
                if (assetRequestItem == null && LibraryRootFolder != null) // Library item
                {
                    assetRequestItem = LibraryRootFolder.FindItem(script);

                    if (assetRequestItem is not null) // Implicitly readable
                        return true;
                }

                // SL is rather harebrained here. In SL, a script you
                // have mod/copy no trans is readable. This subverts
                // permissions, but is used in some products, most
                // notably Hippo door plugin and HippoRent 5 networked
                // prim counter.
                // To enable this broken SL-ism, remove Transfer from
                // the below expressions.
                // Trying to improve on SL perms by making a script
                // readable only if it's really full perms
                //
                if ((assetRequestItem.CurrentPermissions &
                /*
                        ((uint)PermissionMask.Modify |
                        (uint)PermissionMask.Copy |
                        (uint)PermissionMask.Transfer)) !=
                        ((uint)PermissionMask.Modify |
                        (uint)PermissionMask.Copy |
                        (uint)PermissionMask.Transfer))
                */
                        (uint)(PermissionMask.Modify | PermissionMask.Copy)) !=
                        (uint)(PermissionMask.Modify | PermissionMask.Copy))
                    return false;
            }
            else // Prim inventory
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
                if (part is null)
                    return false;

                SceneObjectGroup sog = part.ParentGroup;
                if (sog is null)
                    return false;

                uint perms = GetObjectPermissions(userID, sog, true);
                if((perms & (uint)PermissionMask.Modify) == 0)
                    return false;

                TaskInventoryItem ti = part.Inventory.GetInventoryItem(script);
                if (ti is null) // legacy may not have type
                    return false;

                uint itperms = GetObjectItemPermissions(userID, ti);

                // Require full perms

                if ((itperms &
                /*
                        ((uint)(PermissionMask.Modify |
                        (uint)PermissionMask.Copy |
                        (uint)PermissionMask.Transfer)) !=
                        ((uint)PermissionMask.Modify |
                        (uint)PermissionMask.Copy |
                        (uint)PermissionMask.Transfer))
                */
                        (uint)(PermissionMask.Modify | PermissionMask.Copy)) !=
                        (uint)(PermissionMask.Modify | PermissionMask.Copy))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check whether the specified user can view the given notecard
        /// </summary>
        /// <param name="script"></param>
        /// <param name="objectID"></param>
        /// <param name="user"></param>
        /// <param name="scene"></param>
        /// <returns></returns>
        private bool CanViewNotecard(UUID notecard, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            // A god is a god is a god
            if (IsAdministrator(userID))
                return true;

            if (objectID.IsZero()) // User inventory
            {
                IInventoryService invService = m_scene.InventoryService;
                InventoryItemBase assetRequestItem = invService.GetItem(userID, notecard);
                if (assetRequestItem is null && LibraryRootFolder is not null) // Library item
                {
                    assetRequestItem = LibraryRootFolder.FindItem(notecard);

                    if (assetRequestItem is not null) // Implicitly readable
                        return true;
                }

                // Notecards are always readable unless no copy
                //
                if ((assetRequestItem.CurrentPermissions &
                        (uint)PermissionMask.Copy) !=
                        (uint)PermissionMask.Copy)
                    return false;
            }
            else // Prim inventory
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
                if (part is null)
                    return false;

                SceneObjectGroup sog = part.ParentGroup;
                if (sog is null)
                    return false;

                uint perms = GetObjectPermissions(userID, sog, true);
                if((perms & (uint)PermissionMask.Modify) == 0)
                    return false;

                TaskInventoryItem ti = part.Inventory.GetInventoryItem(notecard);
                if (ti is null)
                    return false;

                uint itperms = GetObjectItemPermissions(userID, ti);

                // Notecards are always readable unless no copy
                //
                if ((itperms &
                        (uint)PermissionMask.Copy) !=
                        (uint)PermissionMask.Copy)
                    return false;
            }

            return true;
        }

        #endregion

        private bool CanLinkObject(UUID userID, UUID objectID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanDelinkObject(UUID userID, UUID objectID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanBuyLand(UUID userID, ILandObject parcel)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return true;
        }

        private bool CanCopyObjectInventory(UUID itemID, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
            if (part is null)
                return false;

            SceneObjectGroup sog = part.ParentGroup;
            if (sog is null)
                return false;

            if(sog.OwnerID.Equals(userID) || IsAdministrator(userID))
                return true;
 
            if(sog.IsAttachment)
                return false;

            if(sog.GroupID.IsZero() || sog.GroupID.NotEqual(sog.OwnerID))
                return false;

            TaskInventoryItem ti = part.Inventory.GetInventoryItem(itemID);
            if(ti is null)
                return false;

            ulong powers = 0;
            if(GroupMemberPowers(sog.GroupID, userID, ref powers))
            {
                if((powers & (ulong)GroupPowers.ObjectManipulate) != 0)
                    return true;

                if((ti.EveryonePermissions & (uint)PermissionMask.Copy) != 0)
                        return true;
            }
            return false;
        }

        // object inventory to object inventory item drag and drop
        private bool CanDoObjectInvToObjectInv(TaskInventoryItem item, SceneObjectPart sourcePart, SceneObjectPart destPart)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);

            if (sourcePart is null || destPart is null || item is null)
                return false;

            if (m_bypassPermissions)
                return true;

            SceneObjectGroup srcsog = sourcePart.ParentGroup;
            SceneObjectGroup destsog = destPart.ParentGroup;
            if (srcsog is null || destsog is null)
                return false;

            uint destsogEffectiveOwnerPerms = destsog.EffectiveOwnerPerms;

            // dest is locked
            if ((destsogEffectiveOwnerPerms & (uint)PermissionMask.Move) == 0)
                return false;

            uint itperms = item.CurrentPermissions;
            uint srcsogEffectiveOwnerPerms = srcsog.EffectiveOwnerPerms;

            // if item is no copy the source is modifed
            if ((itperms & (uint)PermissionMask.Copy) == 0)
            {
                if(srcsog.IsAttachment || destsog.IsAttachment)
                    return false;
                
                if((srcsogEffectiveOwnerPerms & (uint)PermissionMask.Modify) == 0)
                    return false;
            }

            if(srcsog.OwnerID.NotEqual(destsog.OwnerID))
            {
                if((itperms & (uint)PermissionMask.Transfer) == 0)
                    return false;

                if(destsog.IsAttachment && (destsog.RootPart.GetEffectiveObjectFlags() & (uint)PrimFlags.AllowInventoryDrop) == 0)
                    return false;
                if((destsogEffectiveOwnerPerms & (uint)PermissionMask.Modify) == 0)
                    return false;
            }
            else
            {
                if((destsogEffectiveOwnerPerms & (uint)PermissionMask.Modify) == 0 &&
                     (destsog.RootPart.GetEffectiveObjectFlags() & (uint)PrimFlags.AllowInventoryDrop) == 0)
                    return false;
            }

            return true;
        }

        private bool CanDropInObjectInv(InventoryItemBase item, ScenePresence sp, SceneObjectPart destPart)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);

            if (sp is null || sp.IsDeleted || destPart is null || item == null)
                return false;

            SceneObjectGroup destsog = destPart.ParentGroup;
            if (destsog is null || destsog.IsDeleted)
                return false;

            if (m_bypassPermissions)
                return true;

            if(sp.IsGod)
                return true;

            // dest is locked
            if((destsog.EffectiveOwnerPerms & (uint)PermissionMask.Move) == 0)
                return false;

            bool spNotOwner = sp.UUID.NotEqual(destsog.OwnerID);

            // scripts can't be dropped
            if(spNotOwner && item.InvType == (int)InventoryType.LSL)
                return false;

            if(spNotOwner || item.Owner.NotEqual(destsog.OwnerID))
            {
                // no copy item will be moved if it has transfer
                uint itperms = item.CurrentPermissions;
                if((itperms & (uint)PermissionMask.Transfer) == 0)
                    return false;
            }

            // allowdrop is a root part thing and does bypass modify rights
            if((destsog.RootPart.GetEffectiveObjectFlags() & (uint)PrimFlags.AllowInventoryDrop) != 0)
                return true;

            uint perms = GetObjectPermissions(sp.UUID, destsog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;

            return true;
        }

        private bool CanDeleteObjectInventory(UUID itemID, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
            if (part is null)
                return false;

            SceneObjectGroup sog = part.ParentGroup;
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;

            TaskInventoryItem ti = part.Inventory.GetInventoryItem(itemID);
            if(ti is null)
                return false;

            //TODO item perm ?
            return true;
        }

        /// <summary>
        /// Check whether the specified user is allowed to directly create the given inventory type in a prim's
        /// inventory (e.g. the New Script button in the 1.21 Linden Lab client).
        /// </summary>
        /// <param name="invType"></param>
        /// <param name="objectID"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        private bool CanCreateObjectInventory(int invType, UUID objectID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            ScenePresence p = m_scene.GetScenePresence(userID);

            if (p is null)
                return false;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(objectID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(userID, sog, true);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;

            if ((int)InventoryType.LSL == invType)
            {
                if (m_allowedScriptCreators == UserSet.Administrators)
                 return false;
            }

            return true;
        }

        /// <summary>
        /// Check whether the specified user is allowed to create the given inventory type in their inventory.
        /// </summary>
        /// <param name="invType"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        private bool CanCreateUserInventory(int invType, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            if ((int)InventoryType.LSL == invType)
                if (m_allowedScriptCreators == UserSet.Administrators && !IsAdministrator(userID))
                    return false;

            return true;
        }

        /// <summary>
        /// Check whether the specified user is allowed to copy the given inventory type in their inventory.
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        private bool CanCopyUserInventory(UUID itemID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return true;
        }

        /// <summary>
        /// Check whether the specified user is allowed to edit the given inventory item within their own inventory.
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        private bool CanEditUserInventory(UUID itemID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return true;
        }

        /// <summary>
        /// Check whether the specified user is allowed to delete the given inventory item from their own inventory.
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        private bool CanDeleteUserInventory(UUID itemID, UUID userID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return true;
        }

        private bool CanTeleport(UUID userID, Scene scene)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            return true;
        }

        private bool CanResetScript(UUID primID, UUID script, UUID agentID)
        {
            DebugPermissionInformation(MethodInfo.GetCurrentMethod().Name);
            if (m_bypassPermissions) return m_bypassPermissionsValue;

            SceneObjectGroup sog = m_scene.GetGroupByPrim(primID);
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(agentID, sog, false);
            if((perms & (uint)PermissionMask.Modify) == 0) // ??
                return false;
            return true;
        }

        private bool CanCompileScript(UUID ownerUUID, int scriptType)
        {
            //m_log.DebugFormat("check if {0} is allowed to compile {1}", ownerUUID, scriptType);
            return scriptType switch
            {
                0 => GrantLSL.Count == 0 || GrantLSL.ContainsKey(ownerUUID.ToString()),
                1 => GrantCS.Count == 0 || GrantCS.ContainsKey(ownerUUID.ToString()),
                2 => GrantVB.Count == 0 || GrantVB.ContainsKey(ownerUUID.ToString()),
                3 => GrantJS.Count == 0 || GrantJS.ContainsKey(ownerUUID.ToString()),
                4 => GrantYP.Count == 0 || GrantYP.ContainsKey(ownerUUID.ToString()),
                _ => (false),
            };
        }

        private bool CanControlPrimMedia(UUID agentID, UUID primID, int face)
        {
            //m_log.DebugFormat(
            //    "[PERMISSONS]: Performing CanControlPrimMedia check with agentID {0}, primID {1}, face {2}",
            //     agentID, primID, face);

            if (MoapModule is null)
                return false;

            SceneObjectPart part = m_scene.GetSceneObjectPart(primID);
            if (part is null)
                return false;

            MediaEntry me = MoapModule.GetMediaEntry(part, face);

            // If there is no existing media entry then it can be controlled (in this context, created).
            if (me is null)
                return true;

            //m_log.DebugFormat(
            //    "[PERMISSIONS]: Checking CanControlPrimMedia for {0} on {1} face {2} with control permissions {3}",
            //     agentID, primID, face, me.ControlPermissions);

            SceneObjectGroup sog = part.ParentGroup;
            if (sog is null)
                return false;

            uint perms = GetObjectPermissions(agentID, sog, false);
            if((perms & (uint)PermissionMask.Modify) == 0)
                return false;
            return true;
        }

        private bool CanInteractWithPrimMedia(UUID agentID, UUID primID, int face)
        {
            //m_log.DebugFormat(
            //    "[PERMISSONS]: Performing CanInteractWithPrimMedia check with agentID {0}, primID {1}, face {2}",
            //    agentID, primID, face);

            if (MoapModule is null)
                return false;

            SceneObjectPart part = m_scene.GetSceneObjectPart(primID);
            if (part is null)
                return false;

            MediaEntry me = MoapModule.GetMediaEntry(part, face);

            // If there is no existing media entry then it can be controlled (in this context, created).
            if (me is null)
                return true;

            //m_log.DebugFormat(
            //    "[PERMISSIONS]: Checking CanInteractWithPrimMedia for {0} on {1} face {2} with interact permissions {3}",
            //    agentID, primID, face, me.InteractPermissions);

            return GenericPrimMediaPermission(part, agentID, me.InteractPermissions);
        }

        private bool GenericPrimMediaPermission(SceneObjectPart part, UUID agentID, MediaPermission perms)
        {
            //if (IsAdministrator(agentID))
            //   return true;

            if ((perms & MediaPermission.Anyone) == MediaPermission.Anyone)
                return true;

            if ((perms & MediaPermission.Owner) == MediaPermission.Owner)
            {
                if (agentID.Equals(part.OwnerID))
                    return true;
            }

            if ((perms & MediaPermission.Group) == MediaPermission.Group)
            {
                if (IsGroupMember(part.GroupID, agentID, 0))
                    return true;
            }

            return false;
        }
    }
}
