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

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

#pragma warning disable IDE1006

namespace OpenSim.Region.ScriptEngine.Shared.Api.Interfaces
{
    public interface ILSL_Api
    {
        void state(string newState);

                   //ApiDesc Returns absolute version as val (ie as positive value)
       LSL_Integer llAbs(LSL_Integer val);
                   //ApiDesc Returns cosine of val (val in radians)
         LSL_Float llAcos(LSL_Float val);
                   //ApiDesc Sleep 0.1
              void llAddToLandBanList(LSL_Key avatarId, LSL_Float hours);
                    //ApiDesc Sleep 0.1
              void llAddToLandPassList(LSL_Key avatarId, LSL_Float hours);
                   //ApiDesc Sleep 0.1
              void llAdjustSoundVolume(LSL_Float volume);
              void llAllowInventoryDrop(LSL_Integer add);
         LSL_Float llAngleBetween(LSL_Rotation a, LSL_Rotation b);
              void llApplyImpulse(LSL_Vector force, LSL_Integer local);
              void llApplyRotationalImpulse(LSL_Vector force, int local);
                   //ApiDesc Returns sine of val (val in radians)
         LSL_Float llAsin(LSL_Float val);
                   //ApiDesc Returns the angle whose tangent is the y/x
         LSL_Float llAtan2(LSL_Float y, LSL_Float x);
              void llAttachToAvatar(LSL_Integer attachment);
              void llAttachToAvatarTemp(LSL_Integer attachmentPoint);
           LSL_Key llAvatarOnSitTarget();
           LSL_Key llAvatarOnLinkSitTarget(LSL_Integer linknum);
      LSL_Rotation llAxes2Rot(LSL_Vector fwd, LSL_Vector left, LSL_Vector up);
      LSL_Rotation llAxisAngle2Rot(LSL_Vector axis, double angle);
       LSL_Integer llBase64ToInteger(string str);
        LSL_String llBase64ToString(string str);
              void llBreakAllLinks();
              void llBreakLink(int linknum);
          LSL_List llCastRay(LSL_Vector start, LSL_Vector end, LSL_List options);
       LSL_Integer llCeil(double f);
              void llClearCameraParams();
       LSL_Integer llClearLinkMedia(LSL_Integer link, LSL_Integer face);
                   //ApiDesc Sleep 0.1
       LSL_Integer llClearPrimMedia(LSL_Integer face);
                   //ApiDesc Sleep 1.0
              void llCloseRemoteDataChannel(string channel);
         LSL_Float llCloud(LSL_Vector offset);
              void llCollisionFilter(LSL_String name, LSL_Key id, LSL_Integer accept);
              void llCollisionSound(LSL_String impact_sound, LSL_Float impact_volume);
                   //ApiDesc Not Supported - does nothing
              void llCollisionSprite(LSL_String impact_sprite);
         LSL_Float llCos(double f);
                   //ApiDesc Sleep 1.0
              void llCreateLink(LSL_Key targetId, LSL_Integer parent);
          LSL_List llCSV2List(string src);
          LSL_List llDeleteSubList(LSL_List src, int start, int end);
        LSL_String llDeleteSubString(string src, int start, int end);
              void llDetachFromAvatar();
        LSL_Vector llDetectedGrab(int number);
       LSL_Integer llDetectedGroup(int number);
           LSL_Key llDetectedKey(int number);
       LSL_Integer llDetectedLinkNumber(int number);
        LSL_String llDetectedName(int number);
           LSL_Key llDetectedOwner(int number);
        LSL_Vector llDetectedPos(int number);
      LSL_Rotation llDetectedRot(int number);
       LSL_Integer llDetectedType(int number);
        LSL_Vector llDetectedTouchBinormal(int index);
       LSL_Integer llDetectedTouchFace(int index);
        LSL_Vector llDetectedTouchNormal(int index);
        LSL_Vector llDetectedTouchPos(int index);
        LSL_Vector llDetectedTouchST(int index);
        LSL_Vector llDetectedTouchUV(int index);
        LSL_Vector llDetectedVel(int number);
              void llDialog(LSL_Key avatarId, LSL_String message, LSL_List buttons, int chat_channel);
              void llDie();
        LSL_String llDumpList2String(LSL_List src, string seperator);
       LSL_Integer llEdgeOfWorld(LSL_Vector pos, LSL_Vector dir);
                   //ApiDesc Sleep 1.0
              void llEjectFromLand(LSL_Key avatarId);
              void llEmail(string address, string subject, string message);
        LSL_String llEscapeURL(string url);
      LSL_Rotation llEuler2Rot(LSL_Vector v);
         LSL_Float llFabs(double f);
       LSL_Integer llFloor(double f);
              void llForceMouselook(int mouselook);
         LSL_Float llFrand(double mag);
           LSL_Key llGenerateKey();
        LSL_Vector llGetAccel();
       LSL_Integer llGetAgentInfo(LSL_Key id);
        LSL_String llGetAgentLanguage(LSL_Key id);
          LSL_List llGetAgentList(LSL_Integer scope, LSL_List options);
        LSL_Vector llGetAgentSize(LSL_Key id);
         LSL_Float llGetAlpha(int face);
         LSL_Float llGetAndResetTime();
        LSL_String llGetAnimation(LSL_Key id);
          LSL_List llGetAnimationList(LSL_Key id);
       LSL_Integer llGetAttached();
          LSL_List llGetAttachedList(LSL_Key id);
          LSL_List llGetBoundingBox(string obj);
        LSL_Float  llGetCameraAspect();
        LSL_Float  llGetCameraFOV();
        LSL_Vector llGetCameraPos();
      LSL_Rotation llGetCameraRot();
        LSL_Vector llGetCenterOfMass();
        LSL_Vector llGetColor(int face);
        LSL_Key    llGetCreator();
        LSL_String llGetDate();
         LSL_Float llGetEnergy();
        LSL_String llGetEnv(LSL_String name);
        LSL_Vector llGetForce();
       LSL_Integer llGetFreeMemory();
       LSL_Integer llGetUsedMemory();
       LSL_Integer llGetFreeURLs();
        LSL_Vector llGetGeometricCenter();
         LSL_Float llGetGMTclock();
        LSL_String llGetHTTPHeader(LSL_Key request_id, string header);
        LSL_String llGetInventoryAcquireTime(string item);
           LSL_Key llGetInventoryCreator(string item);
           LSL_Key llGetInventoryKey(string name);
        LSL_String llGetInventoryName(int type, int number);
       LSL_Integer llGetInventoryNumber(int type);
       LSL_Integer llGetInventoryPermMask(string item, int mask);
        LSL_String llGetInventoryDesc(string name);
       LSL_Integer llGetInventoryType(string name);
           LSL_Key llGetKey();
           LSL_Key llGetLandOwnerAt(LSL_Vector pos);
           LSL_Key llGetLinkKey(int linknum);
           LSL_Key llGetObjectLinkKey(LSL_Key objectid, int linknum);
        LSL_String llGetLinkName(int linknum);
       LSL_Integer llGetLinkNumber();
       LSL_Integer llGetLinkNumberOfSides(int link);
          LSL_List llGetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules);
          LSL_List llGetLinkPrimitiveParams(int linknum, LSL_List rules);
       LSL_Integer llGetListEntryType(LSL_List src, int index);
       LSL_Integer llGetListLength(LSL_List src);
        LSL_Vector llGetLocalPos();
      LSL_Rotation llGetLocalRot();
         LSL_Float llGetMass();
         LSL_Float llGetMassMKS();
       LSL_Integer llGetMemoryLimit();
              void llGetNextEmail(string address, string subject);
           LSL_Key llGetNotecardLine(string name, int line);
           LSL_Key llGetNumberOfNotecardLines(string name);
        LSL_String llGetNotecardLineSync(string name, int line);
       LSL_Integer llGetNumberOfPrims();
       LSL_Integer llGetNumberOfSides();
        LSL_String llGetObjectDesc();
          LSL_List llGetObjectDetails(LSL_Key objectId, LSL_List args);
         LSL_Float llGetObjectMass(LSL_Key objectId);
        LSL_String llGetObjectName();
       LSL_Integer llGetObjectPermMask(int mask);
       LSL_Integer llGetObjectPrimCount(LSL_Key objectId);
        LSL_Vector llGetOmega();
           LSL_Key llGetOwner();
           LSL_Key llGetOwnerKey(string id);
          LSL_List llGetParcelDetails(LSL_Vector pos, LSL_List param);
       LSL_Integer llGetParcelFlags(LSL_Vector pos);
       LSL_Integer llGetParcelMaxPrims(LSL_Vector pos, int sim_wide);
        LSL_String llGetParcelMusicURL();
       LSL_Integer llGetParcelPrimCount(LSL_Vector pos, int category, int sim_wide);
          LSL_List llGetParcelPrimOwners(LSL_Vector pos);
       LSL_Integer llGetPermissions();
           LSL_Key llGetPermissionsKey();
          LSL_List llGetPrimMediaParams(int face, LSL_List rules);
        LSL_Vector llGetPos();
          LSL_List llGetPrimitiveParams(LSL_List rules);
       LSL_Integer llGetRegionAgentCount();
        LSL_Vector llGetRegionCorner();
       LSL_Integer llGetRegionFlags();
         LSL_Float llGetRegionFPS();
        LSL_String llGetRegionName();
         LSL_Float llGetRegionTimeDilation();
        LSL_Vector llGetRootPosition();
      LSL_Rotation llGetRootRotation();
      LSL_Rotation llGetRot();
        LSL_Vector llGetScale();
        LSL_String llGetScriptName();
       LSL_Integer llGetScriptState(string name);
        LSL_String llGetSimulatorHostname();
       LSL_Integer llGetSPMaxMemory();
       LSL_Integer llGetStartParameter();
       LSL_Integer llGetStatus(int status);
        LSL_String llGetSubString(string src, int start, int end);
        LSL_String llGetTexture(int face);
        LSL_Vector llGetTextureOffset(int face);
         LSL_Float llGetTextureRot(int side);
        LSL_Vector llGetTextureScale(int side);
         LSL_Float llGetTime();
         LSL_Float llGetTimeOfDay();
         LSL_Float llGetRegionTimeOfDay();
        LSL_String llGetTimestamp();
        LSL_Vector llGetTorque();
       LSL_Integer llGetUnixTime();
        LSL_Vector llGetVel();
         LSL_Float llGetWallclock();
              void llGiveInventory(LSL_Key destination, LSL_String inventory);
              void llGiveInventoryList(LSL_Key destination, LSL_String folderName, LSL_List inventory);
       LSL_Integer llGiveMoney(LSL_Key destination, LSL_Integer amount);
           LSL_Key llTransferLindenDollars(LSL_Key destination, LSL_Integer amount);
              void llGodLikeRezObject(string inventory, LSL_Vector pos);
         LSL_Float llGround(LSL_Vector offset);
        LSL_Vector llGroundContour(LSL_Vector offset);
        LSL_Vector llGroundNormal(LSL_Vector offset);
              void llGroundRepel(double height, int water, double tau);
        LSL_Vector llGroundSlope(LSL_Vector offset);
           LSL_Key llHTTPRequest(string url, LSL_List parameters, string body);
              void llHTTPResponse(LSL_Key id, int status, string body);
        LSL_String llInsertString(string dst, int position, string src);
              void llInstantMessage(string user, string message);
        LSL_String llIntegerToBase64(int number);
        LSL_String llKey2Name(LSL_Key id);
        LSL_String llGetUsername(LSL_Key id);
           LSL_Key llRequestUsername(LSL_Key id);
        LSL_String llGetDisplayName(LSL_Key id);
           LSL_Key llRequestDisplayName(LSL_Key id);
              void llLinkParticleSystem(int linknum, LSL_List rules);
              void llLinkSitTarget(LSL_Integer link, LSL_Vector offset, LSL_Rotation rot);
        LSL_String llList2CSV(LSL_List src);
         LSL_Float llList2Float(LSL_List src, int index);
       LSL_Integer llList2Integer(LSL_List src, int index);
           LSL_Key llList2Key(LSL_List src, int index);
          LSL_List llList2List(LSL_List src, int start, int end);
          LSL_List llList2ListStrided(LSL_List src, int start, int end, int stride);
          LSL_List llList2ListSlice(LSL_List src, int start, int end, int stride, int stride_index);
      LSL_Rotation llList2Rot(LSL_List src, int index);
        LSL_String llList2String(LSL_List src, int index);
        LSL_Vector llList2Vector(LSL_List src, int index);
       LSL_Integer llListen(int channelID, string name, string ID, string msg);
              void llListenControl(int number, int active);
              void llListenRemove(int number);
       LSL_Integer llListFindList(LSL_List src, LSL_List test);
       LSL_Integer llListFindListNext(LSL_List src, LSL_List test, LSL_Integer instance);
       LSL_Integer llListFindStrided(LSL_List src, LSL_List test, LSL_Integer lstart, LSL_Integer lend, LSL_Integer lstride);
          LSL_List llListInsertList(LSL_List dest, LSL_List src, int start);
          LSL_List llListRandomize(LSL_List src, int stride);
          LSL_List llListReplaceList(LSL_List dest, LSL_List src, int start, int end);
          LSL_List llListSort(LSL_List src, int stride, int ascending);
          LSL_List llListSortStrided(LSL_List src, int stride, int stride_index, int ascending);
         LSL_Float llListStatistics(int operation, LSL_List src);
              void llLoadURL(string avatar_id, string message, string url);
         LSL_Float llLog(double val);
         LSL_Float llLog10(double val);
              void llLookAt(LSL_Vector target, double strength, double damping);
              void llLoopSound(string sound, double volume);
              void llLoopSoundMaster(string sound, double volume);
              void llLoopSoundSlave(string sound, double volume);
       LSL_Integer llManageEstateAccess(int action, string avatar);
              void llMakeExplosion(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset);
              void llMakeFire(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset);
              void llMakeFountain(int particles, double scale, double vel, double lifetime, double arc, int bounce, string texture, LSL_Vector offset, double bounce_offset);
              void llMakeSmoke(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset);
              void llMapDestination(string simname, LSL_Vector pos, LSL_Vector look_at);
        LSL_String llMD5String(string src, int nonce);
        LSL_String llSHA1String(string src);
        LSL_String llSHA256String(LSL_String src);
              void llMessageLinked(int linknum, int num, string str, string id);
              void llMinEventDelay(double delay);
              void llModifyLand(int action, int brush);
       LSL_Integer llModPow(int a, int b, int c);
              void llMoveToTarget(LSL_Vector target, double tau);
           LSL_Key llName2Key(LSL_String name);
              void llOffsetTexture(double u, double v, int face);
              void llOpenRemoteDataChannel();
       LSL_Integer llOverMyLand(string id);
              void llOwnerSay(string msg);
              void llParcelMediaCommandList(LSL_List commandList);
          LSL_List llParcelMediaQuery(LSL_List aList);
          LSL_List llParseString2List(string str, LSL_List separators, LSL_List spacers);
          LSL_List llParseStringKeepNulls(string src, LSL_List seperators, LSL_List spacers);
              void llParticleSystem(LSL_List rules);
              void llPassCollisions(int pass);
              void llPassTouches(int pass);
              void llPlaySound(string sound, double volume);
              void llPlaySoundSlave(string sound, double volume);
              void llPointAt(LSL_Vector pos);
         LSL_Float llPow(double fbase, double fexponent);
              void llPreloadSound(string sound);
              void llPushObject(string target, LSL_Vector impulse, LSL_Vector ang_impulse, int local);
              void llRefreshPrimURL();
              void llRegionSay(int channelID, string text);
              void llRegionSayTo(string target, int channelID, string text);
              void llReleaseCamera(string avatar);
              void llReleaseControls();
              void llReleaseURL(string url);
              void llRemoteDataReply(string channel, string message_id, string sdata, int idata);
              void llRemoteDataSetRegion();
              void llRemoteLoadScript(string target, string name, int running, int start_param);
              void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param);
              void llRemoveFromLandBanList(string avatar);
              void llRemoveFromLandPassList(string avatar);
              void llRemoveInventory(string item);
              void llRemoveVehicleFlags(int flags);
           LSL_Key llRequestUserKey(LSL_String username);
           LSL_Key llRequestAgentData(string id, int data);
          LSL_List llGetVisualParams(string id, LSL_List visualparams);
           LSL_Key llRequestInventoryData(LSL_String name);
              void llRequestPermissions(string agent, int perm);
           LSL_Key llRequestSecureURL();
           LSL_Key llRequestSimulatorData(string simulator, int data);
         LSL_Float llGetSimStats(LSL_Integer stat_type);
           LSL_Key llRequestURL();
              void llResetLandBanList();
              void llResetLandPassList();
              void llResetOtherScript(string name);
              void llResetScript();
              void llResetTime();
              void llRezAtRoot(string inventory, LSL_Vector position, LSL_Vector velocity, LSL_Rotation rot, int param);
              void llRezObject(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param);
         LSL_Float llRot2Angle(LSL_Rotation rot);
        LSL_Vector llRot2Axis(LSL_Rotation rot);
        LSL_Vector llRot2Euler(LSL_Rotation r);
        LSL_Vector llRot2Fwd(LSL_Rotation r);
        LSL_Vector llRot2Left(LSL_Rotation r);
        LSL_Vector llRot2Up(LSL_Rotation r);
              void llRotateTexture(double rotation, int face);
      LSL_Rotation llRotBetween(LSL_Vector start, LSL_Vector end);
              void llRotLookAt(LSL_Rotation target, double strength, double damping);
       LSL_Integer llRotTarget(LSL_Rotation rot, double error);
              void llRotTargetRemove(int number);
       LSL_Integer llRound(double f);
       LSL_Integer llSameGroup(string agent);
              void llSay(int channelID, string text);
       LSL_Integer llScaleByFactor(double scaling_factor);
        LSL_Float  llGetMaxScaleFactor();
        LSL_Float  llGetMinScaleFactor();
              void llScaleTexture(double u, double v, int face);
       LSL_Integer llScriptDanger(LSL_Vector pos);
              void llScriptProfiler(LSL_Integer flag);
           LSL_Key llSendRemoteData(string channel, string dest, int idata, string sdata);
              void llSensor(string name, string id, int type, double range, double arc);
              void llSensorRemove();
              void llSensorRepeat(string name, string id, int type, double range, double arc, double rate);
              void llSetAlpha(double alpha, int face);
              void llSetBuoyancy(double buoyancy);
              void llSetCameraAtOffset(LSL_Vector offset);
              void llSetCameraEyeOffset(LSL_Vector offset);
              void llSetLinkCamera(LSL_Integer link, LSL_Vector eye, LSL_Vector at);
              void llSetCameraParams(LSL_List rules);
              void llSetClickAction(int action);
              void llSetColor(LSL_Vector color, int face);
              void llSetContentType(LSL_Key id, LSL_Integer type);
              void llSetDamage(double damage);
        LSL_Float llGetHealth(LSL_String key);
              void llSetForce(LSL_Vector force, int local);
              void llSetForceAndTorque(LSL_Vector force, LSL_Vector torque, int local);
              void llSetVelocity(LSL_Vector vel, int local);
              void llSetAngularVelocity(LSL_Vector angularVelocity, int local);
              void llSetHoverHeight(double height, int water, double tau);
              void llSetInventoryPermMask(string item, int mask, int value);
              void llSetLinkAlpha(int linknumber, double alpha, int face);
              void llSetLinkColor(int linknumber, LSL_Vector color, int face);
       LSL_Integer llSetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules);
              void llSetLinkPrimitiveParams(int linknumber, LSL_List rules);
              void llSetLinkTexture(int linknumber, string texture, int face);
              void llSetLinkTextureAnim(int linknum, int mode, int face, int sizex, int sizey, double start, double length, double rate);
              void llSetLocalRot(LSL_Rotation rot);
       LSL_Integer llSetMemoryLimit(LSL_Integer limit);
              void llSetObjectDesc(string desc);
              void llSetObjectName(string name);
              void llSetObjectPermMask(int mask, int value);
              void llSetParcelMusicURL(string url);
              void llSetPayPrice(int price, LSL_List quick_pay_buttons);
              void llSetPos(LSL_Vector pos);
       LSL_Integer llSetRegionPos(LSL_Vector pos);
       LSL_Integer llSetPrimMediaParams(LSL_Integer face, LSL_List rules);
              void llSetPrimitiveParams(LSL_List rules);
              void llSetLinkPrimitiveParamsFast(int linknum, LSL_List rules);
              void llSetPrimURL(string url);
              void llSetRemoteScriptAccessPin(int pin);
              void llSetRot(LSL_Rotation rot);
              void llSetScale(LSL_Vector scale);
              void llSetScriptState(string name, int run);
              void llSetSitText(string text);
              void llSetSoundQueueing(int queue);
              void llSetSoundRadius(double radius);
              void llSetStatus(int status, int value);
              void llSetText(string text, LSL_Vector color, double alpha);
              void llSetTexture(string texture, int face);
              void llSetTextureAnim(int mode, int face, int sizex, int sizey, double start, double length, double rate);
              void llSetTimerEvent(double sec);
              void llSetTorque(LSL_Vector torque, int local);
              void llSetTouchText(string text);
              void llSetVehicleFlags(int flags);
              void llSetVehicleFloatParam(int param, LSL_Float value);
              void llSetVehicleRotationParam(int param, LSL_Rotation rot);
              void llSetVehicleType(int type);
              void llSetVehicleVectorParam(int param, LSL_Vector vec);
              void llShout(int channelID, string text);
         LSL_Float llSin(double f);
              void llSitTarget(LSL_Vector offset, LSL_Rotation rot);
              void llSleep(double sec);
              void llSound(string sound, double volume, int queue, int loop);
              void llSoundPreload(string sound);
         LSL_Float llSqrt(double f);
              void llStartAnimation(string anim);
              void llStopAnimation(string anim);
              void llStartObjectAnimation(string anim);
              void llStopObjectAnimation(string anim);
          LSL_List llGetObjectAnimationNames();
              void llStopHover();
              void llStopLookAt();
              void llStopMoveToTarget();
              void llStopPointAt();
              void llStopSound();
       LSL_Integer llStringLength(string str);
        LSL_String llStringToBase64(string str);
        LSL_String llStringTrim(LSL_String src, LSL_Integer type);
       LSL_Integer llSubStringIndex(string source, string pattern);
              void llTakeCamera(string avatar);
              void llTakeControls(int controls, int accept, int pass_on);
         LSL_Float llTan(double f);
       LSL_Integer llTarget(LSL_Vector position, double range);
              void llTargetOmega(LSL_Vector axis, double spinrate, double gain);
              void llTargetRemove(int number);
              void llTargetedEmail(LSL_Integer target, LSL_String subject, LSL_String message);
              void llTeleportAgentHome(string agent);
              void llTeleportAgent(string agent, string simname, LSL_Vector pos, LSL_Vector lookAt);
              void llTeleportAgentGlobalCoords(string agent, LSL_Vector global, LSL_Vector pos, LSL_Vector lookAt);
              void llTextBox(string avatar, string message, int chat_channel);
        LSL_String llToLower(string source);
        LSL_String llToUpper(string source);
              void llTriggerSound(string sound, double volume);
              void llTriggerSoundLimited(string sound, double volume, LSL_Vector top_north_east, LSL_Vector bottom_south_west);
        LSL_String llUnescapeURL(string url);
              void llUnSit(string id);
         LSL_Float llVecDist(LSL_Vector a, LSL_Vector b);
         LSL_Float llVecMag(LSL_Vector v);
        LSL_Vector llVecNorm(LSL_Vector v);
              void llVolumeDetect(int detect);
         LSL_Float llWater(LSL_Vector offset);
              void llWhisper(int channelID, string text);
        LSL_Vector llWind(LSL_Vector offset);
        LSL_String llXorBase64(string str1, string str2);
        LSL_String llXorBase64Strings(string str1, string str2);
        LSL_String llXorBase64StringsCorrect(string str1, string str2);
       LSL_Integer llGetLinkNumberOfSides(LSL_Integer link);
              void llSetPhysicsMaterial(int material_bits, LSL_Float material_gravity_modifier, LSL_Float material_restitution, LSL_Float material_friction, LSL_Float material_density);
              void SetPrimitiveParamsEx(LSL_Key prim, LSL_List rules, string originFunc);
              void llSetKeyframedMotion(LSL_List frames, LSL_List options);
          LSL_List GetPrimitiveParamsEx(LSL_Key prim, LSL_List rules);
          LSL_List llGetPhysicsMaterial();
              void llSetAnimationOverride(LSL_String animState, LSL_String anim);
              void llResetAnimationOverride(LSL_String anim_state);
        LSL_String llGetAnimationOverride(LSL_String anim_state);
        LSL_String llJsonGetValue(LSL_String json, LSL_List specifiers);
          LSL_List llJson2List(LSL_String json);
        LSL_String llList2Json(LSL_String type, LSL_List values);
        LSL_String llJsonSetValue(LSL_String json, LSL_List specifiers, LSL_String value);
        LSL_String llJsonValueType(LSL_String json, LSL_List specifiers);

        LSL_Integer llGetDayLength();
        LSL_Integer llGetRegionDayLength();
        LSL_Integer llGetDayOffset();
        LSL_Integer llGetRegionDayOffset();
        LSL_Vector llGetSunDirection();
        LSL_Vector llGetRegionSunDirection();
        LSL_Vector llGetMoonDirection();
        LSL_Vector llGetRegionMoonDirection();
        LSL_Rotation llGetSunRotation();
        LSL_Rotation llGetRegionSunRotation();
        LSL_Rotation llGetMoonRotation();
        LSL_Rotation llGetRegionMoonRotation();

         LSL_String llChar(LSL_Integer unicode);
        LSL_Integer llOrd(LSL_String s, LSL_Integer index);
        LSL_Integer llHash(LSL_String s);
         LSL_String llReplaceSubString(LSL_String src, LSL_String pattern, LSL_String replacement, int count);

               void llLinkAdjustSoundVolume(LSL_Integer linknumber, LSL_Float volume);
               void llLinkStopSound(LSL_Integer linknumber);
               void llLinkSetSoundQueueing(int linknumber, int queue);
               void llLinkPlaySound(LSL_Integer linknumber, string sound, double volume);
               void llLinkPlaySound(LSL_Integer linknumber, string sound, double volume, LSL_Integer flags);
               void llLinkSetSoundRadius(int linknumber, double radius);

         LSL_Vector llLinear2sRGB(LSL_Vector src);
         LSL_Vector llsRGB2Linear(LSL_Vector src);
        LSL_Integer llLinksetDataAvailable();
        LSL_Integer llLinksetDataCountFound(LSL_String pattern);
        LSL_Integer llLinksetDataCountKeys();
        LSL_Integer llLinksetDataDelete(LSL_String name);
           LSL_List llLinksetDataDeleteFound(LSL_String pattern, LSL_String pass);
        LSL_Integer llLinksetDataDeleteProtected(LSL_String name, LSL_String pass);
           LSL_List llLinksetDataFindKeys(LSL_String pattern, LSL_Integer start, LSL_Integer count);
           LSL_List llLinksetDataListKeys(LSL_Integer start, LSL_Integer count);
         LSL_String llLinksetDataRead(LSL_String name);
         LSL_String llLinksetDataReadProtected(LSL_String name, LSL_String pass);
               void llLinksetDataReset();
        LSL_Integer llLinksetDataWrite(LSL_String name, LSL_String value);
        LSL_Integer llLinksetDataWriteProtected(LSL_String name, LSL_String value, LSL_String pass);

        LSL_Integer llIsFriend(LSL_Key agent_id);
        LSL_Integer llDerezObject(LSL_Key objectUUID, LSL_Integer flag);

            LSL_Key llRezObjectWithParams(string inventory, LSL_List lparam);
         LSL_String llGetStartString();
        LSL_Integer llGetLinkSitFlags(LSL_Integer linknum);
               void llSetLinkSitFlags(LSL_Integer linknum, LSL_Integer flags);
         LSL_String llHMAC(LSL_String private_key, LSL_String message, LSL_String algo);
         LSL_String llComputeHash(LSL_String message, LSL_String algo);
    }
}
