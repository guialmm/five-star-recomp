// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Ported from VIctory-7900XTX (https://github.com/avatarmanz/VIctory-7900XTX, commit 5f86fa3,
// crates/victory-gpu/src/host/awake.rs): Copyright (c) the VIctory contributors, GPL-3.0-or-later. See
// THIRD_PARTY_NOTICES.md.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// <c>--gpu-keep-awake</c>: keeps the GPU's clocks up between the game's submissions. The GPU stands idle many times a
/// frame for a millisecond or two while the game prepares its next lists; the driver takes that for a light load and
/// lowers the clocks, and what comes next starts slow. VIctory-7900XTX measured a captured frame whose lists take
/// 28.6 ms in one go at 37.8 ms when sent with the game's gaps, and 28.6 ms again with this running (an AMD RDNA3 card).
/// One group of 64 threads counts on a compute queue of ours, over and over (about 2 ms a dispatch at full clocks, at
/// most two in flight): one or two of the GPU's thousands of wave slots. Nothing is sent once the game has submitted
/// nothing for <see cref="QuietMilliseconds"/> (a paused game, a minimized window). <c>--gpu-keep-awake-turns</c> runs
/// it <see cref="TurnMilliseconds"/> on and as long off, for an A/B in one boot; the log names each turn.
/// </summary>
internal static unsafe class GpuKeepAwake
{
    /// <summary>Quiet this long after the game's last submission.</summary>
    public const long QuietMilliseconds = 250;

    /// <summary>With turns: this long on, this long off.</summary>
    public const long TurnMilliseconds = 20_000;

    private const int InFlight = 2;
    private const int TurnKinds = 2;
    private const int SumBytes = 256;
    private const int IdleMilliseconds = 2;
    private const int WaitMilliseconds = 1000;

    private const string Source = """
        #define RS "UAV(u0)"
        RWByteAddressBuffer sum : register(u0);
        [RootSignature(RS)]
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            uint x = id.x;
            [loop] for (uint i = 0; i < COUNT; i++) x = x * 1664525u + 1013904223u;
            sum.Store(id.x * 4u, x);
        }
        """;

    /// <summary>Iterations of the counting loop by default: about 2 ms at full clocks.</summary>
    public const int DefaultLoopCount = 400_000;

    /// <summary>How long <see cref="Stop"/> waits for the thread and the dispatches it sent.</summary>
    private const int StopMilliseconds = 30_000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static long _last;
    private static long _sent;
    private static int _started;
    private static Thread? _thread;
    private static volatile bool _stopping;

    /// <summary>Iterations of the counting loop (a test makes it short: WARP runs it on the CPU).</summary>
    internal static int LoopCount { get; set; } = DefaultLoopCount;

    /// <summary>Off, on, or on and off by turns.</summary>
    public static GpuKeepAwakeMode Mode { get; set; }

    /// <summary>"GPU kept awake: N dispatches sent" (empty when off).</summary>
    public static string Summary
    {
        get
        {
            if (Mode == GpuKeepAwakeMode.Off)
            {
                return string.Empty;
            }

            return $"; GPU kept awake ({Mode}): {Interlocked.Read(ref _sent)} dispatches sent";
        }
    }

    /// <summary>The game submitted to a queue of <paramref name="device"/>: the first time, the thread starts.</summary>
    [HotPath]
    public static void Sent(nint device)
    {
        if (Mode == GpuKeepAwakeMode.Off)
        {
            return;
        }

        long now = Clock.ElapsedMilliseconds;
        long before = Interlocked.Exchange(ref _last, now + 1);

        if (Mode == GpuKeepAwakeMode.Turns && (before == 0 || (before - 1) / TurnMilliseconds != now / TurnMilliseconds))
        {
            NoteTurn(now);
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            Start(device);
        }
    }

    /// <summary>A new turn began: logged with whether it is on.</summary>
    private static void NoteTurn(long now)
    {
        ShimContext.Runtime.Log.Info($"  [gpu awake] turn {now / TurnMilliseconds}: kept awake {On(now)}");
    }

    /// <summary>Whether the GPU is kept awake now: the game submitted within the quiet time, and in its turn.</summary>
    internal static bool Wanted(long now, long last, GpuKeepAwakeMode mode)
    {
        if (mode == GpuKeepAwakeMode.Off || last == 0 || now + 1 >= last + QuietMilliseconds)
        {
            return false;
        }

        return mode != GpuKeepAwakeMode.Turns || On(now);
    }

    /// <summary>In turns, the odd turns are on.</summary>
    private static bool On(long now)
    {
        return now / TurnMilliseconds % TurnKinds == 1;
    }

    /// <summary>
    /// Stops the thread, once the dispatches it sent have run, and lets go of its queue and its reference to the device;
    /// a later submission starts it again. The device factory calls it as its device goes: holding the device kept the
    /// test runner's WARP device, and all a test left on it, alive to the end of the run (E_OUTOFMEMORY at later tests'
    /// device creation on CI).
    /// </summary>
    public static void Stop()
    {
        lock (Gate)
        {
            var thread = _thread;

            if (thread is null)
            {
                return;
            }

            _stopping = true;
            thread.Join(StopMilliseconds);
            _thread = null;
            _stopping = false;
            Interlocked.Exchange(ref _last, 0);
            Interlocked.Exchange(ref _started, 0);
        }
    }

    private static void Start(nint device)
    {
        lock (Gate)
        {
            Marshal.AddRef(device);
            var desktop = new ID3D12Device(device);
            _thread = new Thread(() => Run(desktop)) { IsBackground = true, Name = "fivestar gpu keep-awake" };
            _thread.Start();
        }
    }

    private static void Run(ID3D12Device device)
    {
        try
        {
            Loop(device);
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            ShimContext.Runtime.Log.Warn($"[gpu awake] the GPU is not kept awake: {exception.Message}");
        }
        finally
        {
            device.Dispose();
        }
    }

    private static void Loop(ID3D12Device device)
    {
        var code = Compile();
        using var root = device.CreateRootSignature(0, code);
        using var pipeline = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = root, ComputeShader = code });
        using var queue = device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Compute));

        using var sum = device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(SumBytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
        using var allocator = device.CreateCommandAllocator(CommandListType.Compute);
        using var list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Compute, allocator, pipeline);

        list.SetComputeRootSignature(root);
        list.SetComputeRootUnorderedAccessView(0, sum.GPUVirtualAddress);
        list.Dispatch(1, 1, 1);
        list.Close();

        using var fence = device.CreateFence(0);
        using var completed = new AutoResetEvent(false);
        ShimContext.Runtime.Log.Info($"  [gpu awake] a compute queue keeps the GPU busy while the game submits ({Mode})");
        ulong value = 0;

        while (!_stopping)
        {
            if (!Wanted(Clock.ElapsedMilliseconds, Interlocked.Read(ref _last), Mode))
            {
                Thread.Sleep(IdleMilliseconds);

                continue;
            }

            // Never more than two on the way.
            if (value >= InFlight && fence.CompletedValue + InFlight <= value)
            {
                fence.SetEventOnCompletion(value - 1, completed.SafeWaitHandle.DangerousGetHandle()).CheckError();
                completed.WaitOne(WaitMilliseconds);
                device.DeviceRemovedReason.CheckError();

                continue;
            }

            queue.ExecuteCommandList(list);
            value++;
            queue.Signal(fence, value).CheckError();
            Interlocked.Increment(ref _sent);
        }

        // What was sent runs before the queue and the resources go.
        if (fence.CompletedValue < value)
        {
            fence.SetEventOnCompletion(value, completed.SafeWaitHandle.DangerousGetHandle()).CheckError();
            completed.WaitOne(StopMilliseconds);
        }
    }

    /// <summary>The counting program, compiled with d3dcompiler_47 as the indirect clamp's is.</summary>
    private static byte[] Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#define COUNT {LoopCount}u\n{Source}"));
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        byte[] name = Encoding.ASCII.GetBytes("gpu_keep_awake\0");

        nint code = 0;
        nint errors = 0;
        int hr;

        fixed (byte* text = source, main = entry, profile = target, file = name)
        {
            hr = IndirectClampNative.D3DCompile(text, (nuint)source.Length, file, 0, 0, main, profile, 0, 0, &code, &errors);
        }

        ComObject.Release(errors);

        if (HResult.Failed(hr) || code == 0)
        {
            throw new InvalidOperationException($"the program did not compile ({HResult.Format(hr)})");
        }

        byte* bytes = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(code, IndirectClampNative.BlobPointerSlot))(code);
        nuint size = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(code, IndirectClampNative.BlobSizeSlot))(code);
        byte[] program = new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
        ComObject.Release(code);

        return program;
    }
}
