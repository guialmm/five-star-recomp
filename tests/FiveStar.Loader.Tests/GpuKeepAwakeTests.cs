// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>--gpu-keep-awake: when the compute queue of ours runs.</summary>
public sealed class GpuKeepAwakeTests
{
    private const long Now = 45_000;

    [Fact]
    public void The_gpu_is_kept_awake_only_while_the_game_submits_and_in_its_turn()
    {
        // The game submitted a moment ago (stamps are the time plus one).
        long recent = Now - 10 + 1;
        long stale = Now - GpuKeepAwake.QuietMilliseconds;

        Assert.True(GpuKeepAwake.Wanted(Now, recent, GpuKeepAwakeMode.On));
        Assert.False(GpuKeepAwake.Wanted(Now, stale, GpuKeepAwakeMode.On));
        Assert.False(GpuKeepAwake.Wanted(Now, 0, GpuKeepAwakeMode.On));
        Assert.False(GpuKeepAwake.Wanted(Now, recent, GpuKeepAwakeMode.Off));

        // Turns: the odd 20 s turns are on (45 s is in the third turn, number 2: off; 25 s in turn 1: on).
        long odd = GpuKeepAwake.TurnMilliseconds + 5_000;
        Assert.False(GpuKeepAwake.Wanted(Now, recent, GpuKeepAwakeMode.Turns));
        Assert.True(GpuKeepAwake.Wanted(odd, odd - 10 + 1, GpuKeepAwakeMode.Turns));
    }

    [WarpFact]
    public void A_submission_starts_the_queue_that_keeps_sending_dispatches()
    {
        using var test = TestRuntime.Create();
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);

        // WARP runs the counting loop on the CPU: a short one.
        GpuKeepAwake.LoopCount = ShortLoop;
        GpuKeepAwake.Mode = GpuKeepAwakeMode.On;

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (!GpuKeepAwake.Summary.Contains("dispatches sent", StringComparison.Ordinal) || GpuKeepAwake.Summary.Contains(": 0 dispatches", StringComparison.Ordinal))
            {
                // Each submission of the game's stamps the time the queue runs by.
                GpuKeepAwake.Sent(gpu.Device.NativePointer);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(LongestSeconds), GpuKeepAwake.Summary);
                Thread.Sleep(StepMilliseconds);
            }
        }
        finally
        {
            // The thread, its queue and its reference to the device go before the device does: kept, it held the runner's
            // WARP device for the rest of the run (E_OUTOFMEMORY at later tests' device creation on CI).
            GpuKeepAwake.Stop();
            GpuKeepAwake.Mode = GpuKeepAwakeMode.Off;
            GpuKeepAwake.LoopCount = GpuKeepAwake.DefaultLoopCount;
        }
    }

    private const int LongestSeconds = 30;
    private const int StepMilliseconds = 10;
    private const int ShortLoop = 1_000;
}
