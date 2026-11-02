using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Fusion;
using Fusion.Sockets;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;

namespace DavigoTrueLan;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "dev.codex.davigo.truelan";
    public const string PluginName = "DAVIGO True LAN";
    public const string PluginVersion = "1.0.1";

    internal static ManualLogSource ModLog { get; private set; } = null!;
    internal static ConfigEntry<bool> Enabled { get; private set; } = null!;
    internal static ConfigEntry<string> HostIPv4 { get; private set; } = null!;
    internal static ConfigEntry<int> GamePort { get; private set; } = null!;
    internal static ConfigEntry<int> DiscoveryPort { get; private set; } = null!;
    internal static ConfigEntry<bool> DiscoveryEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> ForceWindowed { get; private set; } = null!;
    internal static ConfigEntry<string> AutoStartMode { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoStartMatch { get; private set; } = null!;

    public override void Load()
    {
        ModLog = Log;
        Enabled = Config.Bind("LAN", "Enabled", true, "Replace DAVIGO online room creation and joining with direct LAN networking.");
        HostIPv4 = Config.Bind("LAN", "HostIPv4", string.Empty, "Client fallback host address. Leave empty to use LAN discovery.");
        GamePort = Config.Bind("LAN", "GamePort", 7542, "Direct Fusion UDP port.");
        DiscoveryPort = Config.Bind("LAN", "DiscoveryPort", 7543, "UDP port used to discover a DAVIGO LAN host.");
        DiscoveryEnabled = Config.Bind("LAN", "DiscoveryEnabled", true, "Broadcast and listen for DAVIGO LAN hosts.");
        ForceWindowed = Config.Bind("Display", "ForceWindowed", false, "Force a 1280x720 window. Useful for local multi-instance testing.");
        AutoStartMode = Config.Bind("Testing", "AutoStartMode", string.Empty, "Optional test mode: Host or Client. Leave empty for normal menu operation.");
        AutoStartMatch = Config.Bind("Testing", "AutoStartMatch", false, "Automatically load the match after a remote role is assigned. Test-only.");

        if (ForceWindowed.Value)
        {
            UnityEngine.Screen.SetResolution(1280, 720, UnityEngine.FullScreenMode.Windowed);
        }

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();
        var patchedMethodCount = Harmony.GetAllPatchedMethods()
            .Count(method => Harmony.GetPatchInfo(method)?.Owners.Contains(PluginGuid) == true);
        if (DiscoveryEnabled.Value)
        {
            LanDiscovery.StartListener(NormalizePort(DiscoveryPort.Value));
        }

        ModLog.LogInfo($"Loaded {PluginName} {PluginVersion} with {patchedMethodCount} patched methods. Photon room startup will be bypassed while Enabled=true.");
    }

    internal static ushort NormalizePort(int port)
    {
        return port is >= 1 and <= ushort.MaxValue ? (ushort)port : (ushort)7542;
    }
}

internal enum LanLaunchRole
{
    None,
    Host,
    Client
}

[HarmonyPatch(typeof(MenuController), "Update")]
internal static class ForceWindowedPatch
{
    private static bool autoStartIssued;

    private static void Prefix()
    {
        if (Plugin.ForceWindowed.Value &&
            (UnityEngine.Screen.fullScreenMode != UnityEngine.FullScreenMode.Windowed ||
             UnityEngine.Screen.width != 1280 ||
             UnityEngine.Screen.height != 720))
        {
            UnityEngine.Screen.SetResolution(1280, 720, UnityEngine.FullScreenMode.Windowed);
        }

        if (autoStartIssued)
        {
            return;
        }

        var mode = Plugin.AutoStartMode.Value.Trim();
        if (mode.Length == 0)
        {
            return;
        }

        var manager = UnityEngine.Object.FindObjectOfType<ConnectionManager>();
        if (manager is null)
        {
            return;
        }

        autoStartIssued = true;
        if (mode.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.ModLog.LogInfo("Test auto-start is opening a LAN host.");
            manager.OpenRoom(Plugin.NormalizePort(Plugin.GamePort.Value), Environment.MachineName, false);
        }
        else if (mode.Equals("Client", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.ModLog.LogInfo("Test auto-start is joining the LAN host as a Warrior.");
            manager.JoinRoom("LAN", PlayerRole.Warrior);
        }
        else
        {
            Plugin.ModLog.LogWarning($"Unknown Testing.AutoStartMode value: {mode}.");
        }
    }
}

internal static class LanState
{
    internal static readonly object Sync = new();
    internal static LanLaunchRole Role { get; set; }
    internal static string? HostAddress { get; set; }
    internal static ushort Port { get; set; }
    internal static PlayerRole RequestedRole { get; set; }
    internal static NetworkRunner? Runner { get; set; }
    internal static bool ClientConnectIssued { get; set; }

    internal static void Reset(LanLaunchRole role, ushort port, string? hostAddress = null, PlayerRole requestedRole = PlayerRole.None)
    {
        lock (Sync)
        {
            Role = role;
            Port = port;
            HostAddress = hostAddress;
            RequestedRole = requestedRole;
            Runner = null;
            ClientConnectIssued = false;
        }
    }
}

[HarmonyPatch(typeof(ConnectionManager), nameof(ConnectionManager.OpenRoom))]
internal static class OpenRoomPatch
{
    private static bool Prefix(ConnectionManager __instance, ushort port, string displayName, bool isPublic)
    {
        if (!Plugin.Enabled.Value)
        {
            return true;
        }

        LanBootstrap.BeginHost(__instance, port, displayName, "OpenRoom");
        return false;
    }
}

[HarmonyPatch(typeof(ConnectionManager), nameof(ConnectionManager.JoinRoom))]
internal static class JoinRoomPatch
{
    private static bool Prefix(ConnectionManager __instance, string name, PlayerRole role)
    {
        if (!Plugin.Enabled.Value)
        {
            return true;
        }

        LanBootstrap.BeginClient(__instance, role, "JoinRoom");
        return false;
    }
}

// OpenRoom/JoinRoom construct their iterator state machines directly in native IL2CPP code.
// Some call sites therefore do not pass through the managed interop wrappers Harmony sees.
// Intercepting the first MoveNext is the reliable point shared by Unity's coroutine runner.
[HarmonyPatch(typeof(ConnectionManager._TryOpenRoom_d__17), nameof(ConnectionManager._TryOpenRoom_d__17.MoveNext))]
internal static class TryOpenRoomMoveNextPatch
{
    private static bool Prefix(ConnectionManager._TryOpenRoom_d__17 __instance, ref bool __result)
    {
        if (!Plugin.Enabled.Value || __instance.__1__state != 0)
        {
            return true;
        }

        __instance.__1__state = -1;
        LanBootstrap.BeginHost(__instance.__4__this, __instance.port, __instance.displayName, "TryOpenRoom.MoveNext");
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ConnectionManager._TryJoinRoom_d__22), nameof(ConnectionManager._TryJoinRoom_d__22.MoveNext))]
internal static class TryJoinRoomMoveNextPatch
{
    private static bool Prefix(ConnectionManager._TryJoinRoom_d__22 __instance, ref bool __result)
    {
        if (!Plugin.Enabled.Value || __instance.__1__state != 0)
        {
            return true;
        }

        __instance.__1__state = -1;
        LanBootstrap.BeginClient(__instance.__4__this, __instance.role, "TryJoinRoom.MoveNext");
        __result = false;
        return false;
    }
}

internal static class LanBootstrap
{
    private static readonly System.Reflection.MethodInfo? OpenLocalRoutineMethod =
        AccessTools.Method(typeof(ConnectionManager), "OpenLocalRoutine");

    internal static void BeginHost(ConnectionManager manager, ushort requestedPort, string displayName, string source)
    {
        // The menu's port belongs to DAVIGO's online flow. LAN mode always uses the
        // explicit mod setting so discovery, firewall rules, and every client agree.
        var lanPort = Plugin.NormalizePort(Plugin.GamePort.Value);
        var localAddress = LanAddressResolver.Resolve();
        LanState.Reset(LanLaunchRole.Host, lanPort, localAddress, PlayerRole.Giant);

        Plugin.ModLog.LogInfo($"LAN host requested via {source}. Photon bypassed; binding UDP {lanPort}, address {localAddress ?? "unknown"}.");
        if (Plugin.DiscoveryEnabled.Value)
        {
            LanDiscovery.StartBeacon(Plugin.NormalizePort(Plugin.DiscoveryPort.Value), lanPort, displayName ?? string.Empty);
        }

        if (!NativeFusionPatch.EnableStandaloneSocketPath())
        {
            const string message = "DAVIGO LAN: this game binary does not match the supported 0.5.2A-1493 build.";
            Plugin.ModLog.LogError(message);
            AppLog.Alert(message);
            return;
        }

        StartLocalRoutine(manager);
    }

    internal static bool BeginClient(ConnectionManager manager, PlayerRole role, string source)
    {
        var endpoint = ResolveHost();
        if (endpoint is null)
        {
            const string message = "DAVIGO LAN: host not found. Start the VR host first or set HostIPv4 in the mod config.";
            Plugin.ModLog.LogError(message);
            AppLog.Alert(message);
            return false;
        }

        LanState.Reset(LanLaunchRole.Client, endpoint.Value.Port, endpoint.Value.Address, role);
        Plugin.ModLog.LogInfo($"LAN client requested via {source} for {endpoint.Value.Address}:{endpoint.Value.Port}, role {role}. Photon bypassed.");
        if (!NativeFusionPatch.EnableStandaloneSocketPath())
        {
            const string message = "DAVIGO LAN: this game binary does not match the supported 0.5.2A-1493 build.";
            Plugin.ModLog.LogError(message);
            AppLog.Alert(message);
            return false;
        }

        StartLocalRoutine(manager);
        return true;
    }

    private static void StartLocalRoutine(ConnectionManager manager)
    {
        if (OpenLocalRoutineMethod is null)
        {
            throw new MissingMethodException(typeof(ConnectionManager).FullName, "OpenLocalRoutine");
        }

        var routine = OpenLocalRoutineMethod.Invoke(manager, null) as Il2CppSystem.Collections.IEnumerator;
        if (routine is null)
        {
            throw new InvalidOperationException("ConnectionManager.OpenLocalRoutine returned no coroutine.");
        }

        manager.StartCoroutine(routine);
    }

    private static (string Address, ushort Port)? ResolveHost()
    {
        var configured = Plugin.HostIPv4.Value.Trim();
        if (IPAddress.TryParse(configured, out var configuredAddress)
            && configuredAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            return (configuredAddress.ToString(), Plugin.NormalizePort(Plugin.GamePort.Value));
        }

        return LanDiscovery.GetLatestHost();
    }
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.Initialize))]
internal static class NetworkRunnerInitializePatch
{
    private static void Prefix(NetworkRunner __instance, ref NetworkRunnerInitializeArgs args)
    {
        if (!Plugin.Enabled.Value || LanState.Role == LanLaunchRole.None)
        {
            return;
        }

        var port = LanState.Port == 0 ? Plugin.NormalizePort(Plugin.GamePort.Value) : LanState.Port;
        if (LanState.Role == LanLaunchRole.Host)
        {
            var bindAddress = NetAddress.Any(port);
            Il2CppRawInitializeArgs.SetAddress(args, 0x08, bindAddress);
            Il2CppRawInitializeArgs.SetInt32(args, 0x48, 5);
            Il2CppRawInitializeArgs.SetInt32(args, 0x50, (int)SimulationModes.Host);

            var address = LanState.HostAddress;
            if (!string.IsNullOrWhiteSpace(address))
            {
                Il2CppRawInitializeArgs.SetAddress(args, 0x28, NetAddress.CreateFromIpPort(address, port));
            }

            Plugin.ModLog.LogInfo($"Initialize rewritten as direct LAN Host on 0.0.0.0:{port} (capacity 5).");
        }
        else
        {
            Il2CppRawInitializeArgs.SetAddress(args, 0x08, NetAddress.Any(0));
            Il2CppRawInitializeArgs.SetInt32(args, 0x48, 5);
            Il2CppRawInitializeArgs.SetInt32(args, 0x50, (int)SimulationModes.Client);
            Plugin.ModLog.LogInfo("Initialize rewritten as direct LAN Client (capacity 5).");
        }

        Plugin.ModLog.LogInfo($"Initialize args verified from native memory: modeHasValue={Il2CppRawInitializeArgs.HasValue(args, 0x50, 4)}, mode={Il2CppRawInitializeArgs.ReadInt32(args, 0x50)}, playerCountHasValue={Il2CppRawInitializeArgs.HasValue(args, 0x48, 4)}, playerCount={Il2CppRawInitializeArgs.ReadInt32(args, 0x48)}.");

        LanState.Runner = __instance;
    }
}

internal static class Il2CppRawInitializeArgs
{
    // Layout from DAVIGO 0.5.2A-1493 IL2CPP metadata:
    // Nullable<NetAddress> is 0x20 bytes (0x18-byte value + HasValue),
    // Nullable<int/SimulationModes> is 0x08 bytes (4-byte value + HasValue).
    internal static unsafe void SetAddress(NetworkRunnerInitializeArgs args, int fieldOffset, NetAddress value)
    {
        var destination = GetValuePointer(args) + fieldOffset;
        var source = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
        Buffer.MemoryCopy(source, destination, 0x18, 0x18);
        destination[0x18] = 1;
    }

    internal static unsafe void SetInt32(NetworkRunnerInitializeArgs args, int fieldOffset, int value)
    {
        var destination = GetValuePointer(args) + fieldOffset;
        *(int*)destination = value;
        destination[4] = 1;
    }

    internal static unsafe int ReadInt32(NetworkRunnerInitializeArgs args, int fieldOffset)
    {
        return *(int*)(GetValuePointer(args) + fieldOffset);
    }

    internal static unsafe bool HasValue(NetworkRunnerInitializeArgs args, int fieldOffset, int valueSize)
    {
        return GetValuePointer(args)[fieldOffset + valueSize] != 0;
    }

    private static unsafe byte* GetValuePointer(NetworkRunnerInitializeArgs args)
    {
        var boxed = IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)args);
        return (byte*)IL2CPP.il2cpp_object_unbox(boxed);
    }
}

internal static class NativeFusionPatch
{
    private const int InitializeArgsIsSinglePlayerRva = 0x00DA9800;
    private const int NetworkRunnerIsSinglePlayerRva = 0x0116A760;
    private const int FirstIsSinglePlayerCallRva = 0x0115EA55;
    private const int SecondIsSinglePlayerCallRva = 0x0115EB1B;
    private const int RoomRequestRolesIsSinglePlayerCallRva = 0x0080168A;
    private const int RoomPlayerJoinedCloudCheckRva = 0x008002E3;
    private const int RoomPlayerJoinedAfterCloudCheckRva = 0x00800336;
    private const int StandaloneSocketMetadataLoadRva = 0x0115E81A;
    private const int InitializeMetadataCallRva = 0x0115E821;
    private const int StandaloneSocketClassLoadRva = 0x0115EBF6;
    private const uint PageExecuteReadWrite = 0x40;
    private static readonly object Sync = new();
    private static bool applied;

    internal static bool EnableStandaloneSocketPath()
    {
        lock (Sync)
        {
            if (applied)
            {
                return true;
            }

            var module = GetModuleHandle("GameAssembly.dll");
            if (module == IntPtr.Zero)
            {
                Plugin.ModLog.LogError("GameAssembly.dll is not loaded.");
                return false;
            }

            var socketClassLoad = IntPtr.Add(module, StandaloneSocketClassLoadRva);
            if (Marshal.ReadByte(socketClassLoad) != 0x48 ||
                Marshal.ReadByte(socketClassLoad, 1) != 0x8B ||
                Marshal.ReadByte(socketClassLoad, 2) != 0x0D)
            {
                Plugin.ModLog.LogError("Unexpected standalone socket class load instruction.");
                return false;
            }

            var classSlot = IntPtr.Add(socketClassLoad, 7 + Marshal.ReadInt32(socketClassLoad, 3));
            var metadataLoad = IntPtr.Add(module, StandaloneSocketMetadataLoadRva);
            if (Marshal.ReadByte(metadataLoad) != 0x48 ||
                Marshal.ReadByte(metadataLoad, 1) != 0x8D ||
                Marshal.ReadByte(metadataLoad, 2) != 0x0D ||
                IntPtr.Add(metadataLoad, 7 + Marshal.ReadInt32(metadataLoad, 3)) != classSlot)
            {
                Plugin.ModLog.LogError("Standalone socket metadata slot does not match its runtime load.");
                return false;
            }

            var metadataCall = IntPtr.Add(module, InitializeMetadataCallRva);
            if (Marshal.ReadByte(metadataCall) != 0xE8)
            {
                Plugin.ModLog.LogError("Unexpected metadata initializer call opcode.");
                return false;
            }

            var relativeTarget = Marshal.ReadInt32(metadataCall, 1);
            var initializeMetadata = IntPtr.Add(metadataCall, 5 + relativeTarget);
            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeRuntimeMetadataDelegate>(initializeMetadata);
            initialize(classSlot);

            var nullSocketClass = Il2CppClassPointerStore<NetSocketNull>.NativeClassPtr;
            var nativeSocketClass = Il2CppClassPointerStore<NetSocketNative>.NativeClassPtr;
            var currentSocketClass = Marshal.ReadIntPtr(classSlot);
            if (currentSocketClass != nullSocketClass && currentSocketClass != nativeSocketClass)
            {
                Plugin.ModLog.LogError($"Unexpected standalone socket class pointer 0x{currentSocketClass.ToInt64():X}.");
                return false;
            }

            if (currentSocketClass != nativeSocketClass)
            {
                if (!VirtualProtect(classSlot, (UIntPtr)IntPtr.Size, PageExecuteReadWrite, out var oldSlotProtection))
                {
                    Plugin.ModLog.LogError($"VirtualProtect for socket class slot failed with Win32 error {Marshal.GetLastWin32Error()}.");
                    return false;
                }

                try
                {
                    Marshal.WriteIntPtr(classSlot, nativeSocketClass);
                }
                finally
                {
                    VirtualProtect(classSlot, (UIntPtr)IntPtr.Size, oldSlotProtection, out _);
                }
            }

            if (!PatchIsSinglePlayerCall(module, FirstIsSinglePlayerCallRva, InitializeArgsIsSinglePlayerRva) ||
                !PatchIsSinglePlayerCall(module, SecondIsSinglePlayerCallRva, InitializeArgsIsSinglePlayerRva) ||
                !PatchIsSinglePlayerCall(module, RoomRequestRolesIsSinglePlayerCallRva, NetworkRunnerIsSinglePlayerRva) ||
                !PatchRoomPlayerJoinedCloudCheck(module))
            {
                return false;
            }

            applied = true;
            Plugin.ModLog.LogInfo("Fusion standalone NetSocketNative path enabled; Initialize and room-role RPC bypass cloud-only branches.");
            return true;
        }
    }

    private static bool PatchIsSinglePlayerCall(IntPtr module, int callRva, int expectedTargetRva)
    {
        var call = IntPtr.Add(module, callRva);
        if (Marshal.ReadByte(call) != 0xE8 ||
            IntPtr.Add(call, 5 + Marshal.ReadInt32(call, 1)) != IntPtr.Add(module, expectedTargetRva))
        {
            Plugin.ModLog.LogError($"Unexpected IsSinglePlayer call at RVA 0x{callRva:X}.");
            return false;
        }

        if (!VirtualProtect(call, (UIntPtr)5, PageExecuteReadWrite, out var oldProtection))
        {
            Plugin.ModLog.LogError($"VirtualProtect failed at RVA 0x{callRva:X} with Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }

        try
        {
            // mov al, 1; nop; nop; nop -- bypass only this verified call site.
            Marshal.Copy(new byte[] { 0xB0, 0x01, 0x90, 0x90, 0x90 }, 0, call, 5);
            FlushInstructionCache(GetCurrentProcess(), call, (UIntPtr)5);
        }
        finally
        {
            VirtualProtect(call, (UIntPtr)5, oldProtection, out _);
        }

        return true;
    }

    private static bool PatchRoomPlayerJoinedCloudCheck(IntPtr module)
    {
        var start = IntPtr.Add(module, RoomPlayerJoinedCloudCheckRva);
        var expected = new byte[] { 0x48, 0x85, 0xFF, 0x0F, 0x84 };
        for (var index = 0; index < expected.Length; index++)
        {
            if (Marshal.ReadByte(start, index) != expected[index])
            {
                Plugin.ModLog.LogError("Unexpected Room.OnPlayerJoined cloud-check prologue.");
                return false;
            }
        }

        const int patchSize = 9;
        var relativeJump = RoomPlayerJoinedAfterCloudCheckRva - (RoomPlayerJoinedCloudCheckRva + 5);
        var patch = new byte[patchSize];
        patch[0] = 0xE9;
        BitConverter.GetBytes(relativeJump).CopyTo(patch, 1);
        for (var index = 5; index < patch.Length; index++)
        {
            patch[index] = 0x90;
        }

        if (!VirtualProtect(start, (UIntPtr)patch.Length, PageExecuteReadWrite, out var oldProtection))
        {
            Plugin.ModLog.LogError($"VirtualProtect failed at RVA 0x{RoomPlayerJoinedCloudCheckRva:X} with Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }

        try
        {
            Marshal.Copy(patch, 0, start, patch.Length);
            FlushInstructionCache(GetCurrentProcess(), start, (UIntPtr)patch.Length);
        }
        finally
        {
            VirtualProtect(start, (UIntPtr)patch.Length, oldProtection, out _);
        }

        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitializeRuntimeMetadataDelegate(IntPtr metadataSlot);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtection, out uint oldProtection);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.SetInitializationDone))]
internal static class NetworkRunnerInitializationDonePatch
{
    private static void Postfix(NetworkRunner __instance)
    {
        if (!Plugin.Enabled.Value || LanState.Role != LanLaunchRole.Client || LanState.ClientConnectIssued)
        {
            return;
        }

        var host = LanState.HostAddress;
        if (string.IsNullOrWhiteSpace(host))
        {
            Plugin.ModLog.LogError("Client initialization completed without a LAN host address.");
            return;
        }

        try
        {
            LanState.ClientConnectIssued = true;
            var address = NetAddress.CreateFromIpPort(host, LanState.Port);
            var token = new Il2CppStructArray<byte>(Encoding.ASCII.GetBytes("DAVIGO_TRUE_LAN"));
            var uniqueId = new Il2CppStructArray<byte>(Guid.NewGuid().ToByteArray());
            __instance.Connect(address, token, uniqueId);
            Plugin.ModLog.LogInfo($"Direct Connect issued to {host}:{LanState.Port}.");
        }
        catch (Exception exception)
        {
            LanState.ClientConnectIssued = false;
            Plugin.ModLog.LogError($"Direct Connect failed: {exception}");
        }
    }
}

[HarmonyPatch]
internal static class NetworkRunnerConnectionRequestPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.GetDeclaredMethods(typeof(NetworkRunner))
            .First(method => method.Name.EndsWith("OnConnectionRequest", StringComparison.Ordinal));

    private static void Postfix(object[] __args, ref bool __result)
    {
        if (!Plugin.Enabled.Value || LanState.Role != LanLaunchRole.Host)
        {
            return;
        }

        var token = __args.Length > 1 ? __args[1] as Il2CppStructArray<byte> : null;
        var isLanToken = token is not null &&
                         Encoding.ASCII.GetString(token.ToArray()).StartsWith("DAVIGO_TRUE_LAN", StringComparison.Ordinal);
        if (isLanToken)
        {
            __result = true;
        }

        Plugin.ModLog.LogInfo($"Connection request from {(__args.Length > 0 ? __args[0] : "unknown")}: LAN token={isLanToken}, accepted={__result}.");
    }
}

[HarmonyPatch]
internal static class NetworkRunnerConnectedPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.GetDeclaredMethods(typeof(NetworkRunner))
            .First(method => method.Name.EndsWith("OnConnectedToServer", StringComparison.Ordinal));

    private static void Postfix(NetworkRunner __instance)
    {
        Plugin.ModLog.LogInfo("Fusion connected to LAN server.");
        LanRoleAssignment.TryRequest(__instance, "connection completed");
    }
}

[HarmonyPatch]
internal static class NetworkRunnerConnectionFailedPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.GetDeclaredMethods(typeof(NetworkRunner))
            .First(method => method.Name.EndsWith("OnConnectionFailed", StringComparison.Ordinal));

    private static void Postfix(object[] __args) =>
        Plugin.ModLog.LogError($"Fusion LAN connection failed: {string.Join(", ", __args.Select(value => value?.ToString() ?? "null"))}.");
}

[HarmonyPatch]
internal static class NetworkRunnerPlayerJoinedPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.GetDeclaredMethods(typeof(NetworkRunner))
            .First(method => method.Name.EndsWith("PlayerJoined", StringComparison.Ordinal));

    private static void Postfix(object[] __args) =>
        Plugin.ModLog.LogInfo($"Fusion player joined: {(__args.Length > 0 ? __args[0] : "unknown")}.");
}

[HarmonyPatch(typeof(Govidad.Room), "OnPlayerJoined")]
internal static class RoomPlayerJoinedPatch
{
    private static void Prefix(PlayerRef player) =>
        Plugin.ModLog.LogInfo($"DAVIGO Room is registering Fusion player {player}.");
}

[HarmonyPatch(typeof(Govidad.Room), nameof(Govidad.Room.RpcRequestRoles))]
internal static class RoomRequestRolesPatch
{
    private static void Prefix(Govidad.Room __instance, PlayerRef player, Il2CppStructArray<PlayerRole> roles)
    {
        if (LanState.Role == LanLaunchRole.Host)
        {
            __instance.IsAllowWarriorJoins = true;
            __instance.WarriorCap = 4;

            // OpenLocalRoutine normally prepares split-screen roles and can add a
            // local Warrior to the VR host. A LAN host owns only the Giant; remote
            // PlayerRefs keep their own Warrior requests unchanged.
            if (LanState.Runner is not null && player == LanState.Runner.LocalPlayer)
            {
                for (var index = 0; index < roles.Length; index++)
                {
                    roles[index] = index == 0 ? PlayerRole.Giant : PlayerRole.None;
                }
            }
        }

        if (LanState.Role == LanLaunchRole.Client && LanState.RequestedRole != PlayerRole.None)
        {
            for (var index = 0; index < roles.Length; index++)
            {
                roles[index] = index == 0 ? LanState.RequestedRole : PlayerRole.None;
            }
        }

        Plugin.ModLog.LogInfo($"DAVIGO role RPC invoked for {player}: [{string.Join(", ", roles.ToArray())}].");
    }
}

[HarmonyPatch(typeof(Govidad.RoomController), nameof(Govidad.RoomController.InitializeRoom))]
internal static class RoomInitializedPatch
{
    private static void Postfix(Govidad.RoomController __instance)
    {
        if (LanState.Role == LanLaunchRole.Host && __instance.Room is not null)
        {
            __instance.Room.IsAllowWarriorJoins = true;
            __instance.Room.WarriorCap = 4;
            Plugin.ModLog.LogInfo("LAN room now accepts up to four Warrior joins.");
        }

        LanRoleAssignment.TryRequest(__instance.Runner, "room initialized", __instance);
    }
}

[HarmonyPatch(typeof(Govidad.RoomController), "Room_OnRoleAssigned")]
internal static class RoomRoleAssignedPatch
{
    private static bool matchAutoStartIssued;

    private static void Postfix(Govidad.RoomController __instance, Govidad.RoomUser user)
    {
        if (user.Role != PlayerRole.None)
        {
            Plugin.ModLog.LogInfo($"DAVIGO assigned role {user.Role} to Fusion player {user.Player} ({user.Username}).");
        }

        if (Plugin.AutoStartMatch.Value &&
            !matchAutoStartIssued &&
            LanState.Role == LanLaunchRole.Host &&
            user.Role == PlayerRole.Warrior)
        {
            matchAutoStartIssued = true;
            var loaded = __instance.LoadMatch();
            Plugin.ModLog.LogInfo($"Test auto-start requested match loading after remote role assignment: {loaded}.");
        }
    }
}

[HarmonyPatch(typeof(Govidad.RoomController), "OnBeforeBattleSceneActive")]
internal static class BattleSceneActivatingPatch
{
    private static void Prefix() =>
        Plugin.ModLog.LogInfo("DAVIGO LAN battle scene is activating.");
}

[HarmonyPatch(typeof(Govidad.RoomController), "OnBattleReady")]
internal static class BattleSceneReadyPatch
{
    private static void Postfix() =>
        Plugin.ModLog.LogInfo("DAVIGO LAN battle scene is ready.");
}

internal static class LanRoleAssignment
{
    internal static void TryRequest(NetworkRunner runner, string source, Govidad.RoomController? controller = null)
    {
        if (!Plugin.Enabled.Value || LanState.Role != LanLaunchRole.Client || LanState.RequestedRole == PlayerRole.None)
        {
            return;
        }

        try
        {
            controller ??= runner.GetComponent<Govidad.RoomController>();
            controller ??= UnityEngine.Object.FindObjectOfType<Govidad.RoomController>();
            if (controller is null)
            {
                Plugin.ModLog.LogWarning($"Could not request LAN role after {source}: RoomController not found.");
                return;
            }

            var roles = new Il2CppStructArray<PlayerRole>(1);
            roles[0] = LanState.RequestedRole;
            controller.RequestRoles(runner.LocalPlayer, Govidad.RoomUser.CreateRoleArray(roles));
            Plugin.ModLog.LogInfo($"Requested LAN role {LanState.RequestedRole} for player {runner.LocalPlayer} after {source}.");
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"Could not request the selected LAN role after {source}: {exception.Message}");
        }
    }
}

internal static class LanDiscovery
{
    private const string Magic = "DAVIGO_TRUE_LAN_V1";
    private static readonly object Sync = new();
    private static CancellationTokenSource? listenerCancellation;
    private static CancellationTokenSource? beaconCancellation;
    private static (string Address, ushort Port, DateTime Seen)? latestHost;

    internal static void StartListener(ushort discoveryPort)
    {
        lock (Sync)
        {
            if (listenerCancellation is not null)
            {
                return;
            }

            listenerCancellation = new CancellationTokenSource();
            _ = Task.Run(() => Listen(discoveryPort, listenerCancellation.Token));
        }
    }

    internal static void StartBeacon(ushort discoveryPort, ushort gamePort, string displayName)
    {
        lock (Sync)
        {
            beaconCancellation?.Cancel();
            beaconCancellation = new CancellationTokenSource();
            var advertisedAddress = LanState.HostAddress ?? string.Empty;
            _ = Task.Run(() => Broadcast(discoveryPort, gamePort, advertisedAddress, displayName, beaconCancellation.Token));
        }
    }

    internal static (string Address, ushort Port)? GetLatestHost()
    {
        lock (Sync)
        {
            if (latestHost is not { } host || DateTime.UtcNow - host.Seen > TimeSpan.FromSeconds(8))
            {
                return null;
            }

            return (host.Address, host.Port);
        }
    }

    private static async Task Listen(ushort port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var message = Encoding.UTF8.GetString(result.Buffer);
                var parts = message.Split('|');
                if (parts.Length < 3 || parts[0] != Magic || parts[1] != "0.5.2A-1493" || !ushort.TryParse(parts[2], out var gamePort))
                {
                    continue;
                }

                var address = parts.Length >= 4 &&
                              IPAddress.TryParse(parts[3], out var advertisedAddress) &&
                              advertisedAddress.AddressFamily == AddressFamily.InterNetwork
                    ? advertisedAddress.ToString()
                    : result.RemoteEndPoint.Address.ToString();
                var changed = false;
                lock (Sync)
                {
                    changed = latestHost?.Address != address || latestHost?.Port != gamePort;
                    latestHost = (address, gamePort, DateTime.UtcNow);
                }

                if (changed)
                {
                    Plugin.ModLog.LogInfo($"Discovered DAVIGO LAN host at {address}:{gamePort}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"LAN discovery listener stopped: {exception.Message}");
        }
    }

    private static async Task Broadcast(ushort discoveryPort, ushort gamePort, string hostAddress, string displayName, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var endpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            var safeName = displayName.Replace('|', ' ');
            var payload = Encoding.UTF8.GetBytes($"{Magic}|0.5.2A-1493|{gamePort}|{hostAddress}|{safeName}");

            while (!cancellationToken.IsCancellationRequested)
            {
                await client.SendAsync(payload, payload.Length, endpoint).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"LAN discovery beacon stopped: {exception.Message}");
        }
    }
}

internal static class LanAddressResolver
{
    internal static string? Resolve()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Select(address => (Adapter: adapter, Address: address.Address)))
            .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(entry => !IPAddress.IsLoopback(entry.Address))
            .OrderBy(entry => IsLikelyVirtual(entry.Adapter))
            .ThenByDescending(entry => HasIpv4Gateway(entry.Adapter))
            .ThenByDescending(entry => IsPrivate(entry.Address))
            .ThenBy(entry => entry.Address.ToString(), StringComparer.Ordinal)
            .FirstOrDefault()
            .Address
            ?.ToString();
    }

    private static bool HasIpv4Gateway(NetworkInterface adapter) =>
        adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
            !gateway.Address.Equals(IPAddress.Any));

    private static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return new[] { "vpn", "tunnel", "tap", "tun", "wireguard", "tailscale", "zerotier", "virtual", "vmware", "hyper-v", "wsl" }
            .Any(identity.Contains);
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
