using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Celeste;
using Celeste.Mod;
using Celeste.Mod.SpeedrunTool.Other;
using Celeste.Mod.SpeedrunTool.SaveLoad;
using Microsoft.Xna.Framework.Input;
using Monocle;
using TAS.EverestInterop;
using TAS.EverestInterop.Hitboxes;
using TAS.EverestInterop.InfoHUD;
using TAS.Gameplay;
using TAS.InfoHUD;
using TAS.Input.Commands;
using TAS.Module;
using TAS.Utils;

namespace TAS.ModInterop;

public static class SpeedrunToolInterop {
    public static bool Installed { get; private set; }

    private static object saveLoadAction;
    private static Dictionary<Entity, EntityData> savedEntityData;
    private static int groupCounter;
    private static bool simulatePauses;
    private static bool pauseOnCurrentFrame;
    private static int skipFrames;
    private static int waitingFrames;
    private static StunPauseCommand.StunPauseMode? localMode;
    private static StunPauseCommand.StunPauseMode? globalModeRuntime;
    private static HashSet<Keys> pressKeys;
    private static long? tasStartFileTime;
    private static MouseState mouseState;
    private static Dictionary<Follower, bool> followers;
    private static bool disallowUnsafeInput;
    private static Random auraRandom;
    private static bool betterInvincible = false;

    [Load]
    private static void Load() {
        Installed = ModUtils.IsInstalled("SpeedrunTool");
        Everest.Events.AssetReload.OnBeforeReload += _ => Installed = false;
        Everest.Events.AssetReload.OnAfterReload += _ => Installed = ModUtils.IsInstalled("SpeedrunTool");
    }

    public static void AddSaveLoadAction() {
        Action<Dictionary<Type, Dictionary<string, object>>, Level> save = (_, _) => {
            savedEntityData = EntityDataHelper.CachedEntityData.DeepCloneShared();
            InfoWatchEntity.WatchedEntities_Save = InfoWatchEntity.WatchedEntities.DeepCloneShared();
            groupCounter = CycleHitboxColor.GroupCounter;
            simulatePauses = StunPauseCommand.SimulatePauses;
            pauseOnCurrentFrame = StunPauseCommand.PauseOnCurrentFrame;
            skipFrames = StunPauseCommand.SkipFrames;
            waitingFrames = StunPauseCommand.WaitingFrames;
            localMode = StunPauseCommand.LocalMode;
            globalModeRuntime = StunPauseCommand.GlobalModeRuntime;
            pressKeys = PressCommand.PressKeys.DeepCloneShared();
            tasStartFileTime = MetadataCommands.TasStartFileTime;
            mouseState = MouseCommand.CurrentState;
            followers = HitboxSimplified.Followers.DeepCloneShared();
            disallowUnsafeInput = SafeCommand.DisallowUnsafeInput;
            auraRandom = DesyncFixer.AuraHelperSharedRandom.DeepCloneShared();
            betterInvincible = Manager.Running && BetterInvincible.Invincible;
        };
        Action<Dictionary<Type, Dictionary<string, object>>, Level> load = (_, _) => {
            EntityDataHelper.CachedEntityData = savedEntityData.DeepCloneShared();
            InfoWatchEntity.WatchedEntities = InfoWatchEntity.WatchedEntities_Save.DeepCloneShared();
            CycleHitboxColor.GroupCounter = groupCounter;
            StunPauseCommand.SimulatePauses = simulatePauses;
            StunPauseCommand.PauseOnCurrentFrame = pauseOnCurrentFrame;
            StunPauseCommand.SkipFrames = skipFrames;
            StunPauseCommand.WaitingFrames = waitingFrames;
            StunPauseCommand.LocalMode = localMode;
            StunPauseCommand.GlobalModeRuntime = globalModeRuntime;
            PressCommand.PressKeys.Clear();
            foreach (Keys keys in pressKeys) {
                PressCommand.PressKeys.Add(keys);
            }

            MetadataCommands.TasStartFileTime = tasStartFileTime;
            MouseCommand.CurrentState = mouseState;
            HitboxSimplified.Followers = followers.DeepCloneShared();
            SafeCommand.DisallowUnsafeInput = disallowUnsafeInput;
            DesyncFixer.AuraHelperSharedRandom = auraRandom.DeepCloneShared();
            BetterInvincible.Invincible = Manager.Running && betterInvincible;
        };
        Action clear = () => {
            savedEntityData = null;
            pressKeys = null;
            followers = null;
            InfoWatchEntity.WatchedEntities_Save.Clear();
            auraRandom = null;
            betterInvincible = false;
        };

        ConstructorInfo constructor = typeof(SaveLoadAction).GetConstructors()[0];
        Type delegateType = constructor.GetParameters()[0].ParameterType;

        saveLoadAction = constructor.Invoke(new object[] {
                save.Method.CreateDelegate(delegateType, save.Target),
                load.Method.CreateDelegate(delegateType, load.Target),
                clear,
                null,
                null
            }
        );
        SaveLoadAction.Add((SaveLoadAction) saveLoadAction);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ClearSaveLoadAction() {
        if (saveLoadAction != null) {
            SaveLoadAction.Remove((SaveLoadAction) saveLoadAction);
        }
    }

    public static void InputDeregister() {
        Dictionary<Hotkey, HotkeyConfig> hotkeyConfigs = typeof(HotkeyConfigUi).GetFieldValue<Dictionary<Hotkey, HotkeyConfig>>("HotkeyConfigs");
        foreach (HotkeyConfig config in hotkeyConfigs.Values) {
            config.VirtualButton.Value.Deregister();
        }
    }
}
