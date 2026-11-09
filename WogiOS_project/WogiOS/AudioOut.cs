using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AVFoundation;
using Foundation;
using WogShim;

namespace WogiOS;

internal sealed unsafe class AudioOut : IAudioSink
{
    readonly AVAudioEngine _engine = new();
    readonly AVAudioPlayerNode _node = new();
    readonly AVAudioFormat _fmt;
    readonly int _rate, _ch, _bits, _frameBytes;
    long _scheduled, _played;
    int _gen, _writes;
    volatile bool _disposed;
    readonly ManualResetEventSlim _run = new(true);   // 백그라운드에서는 Reset → Write 가 멈춘다
    readonly object _startLock = new();

    // ── 앱이 백그라운드로 가면 모든 오디오 출력을 멈추고, 돌아오면 이어서 재생 ──
    static readonly List<AudioOut> Live = new();
    static volatile bool _globalPaused;

    public static void SetPaused(bool paused)
    {
        if (_globalPaused == paused) return;   // 같은 알림이 여러 경로로 와도 한 번만 처리
        _globalPaused = paused;
        AudioOut[] snap;
        lock (Live) snap = Live.ToArray();

        if (!paused) { try { AVAudioSession.SharedInstance().SetActive(true, out _); } catch { } }
        foreach (var a in snap) { if (paused) a.Pause(); else a.Resume(); }
        if (paused) { try { AVAudioSession.SharedInstance().SetActive(false, out _); } catch { } }
    }

    void Pause()
    {
        _run.Reset();
        try { _node.Pause(); _engine.Pause(); Log("paused (background)"); } catch (Exception e) { Log("pause failed: " + e.Message); }
    }

    void Resume()
    {
        if (_disposed) return;
        _run.Set();
        try { Start(); Log("resumed (foreground)"); } catch (Exception e) { Log("resume failed: " + e.Message); }
    }

    static void Log(string s)
    {
        try
        {
            string docs = NSFileManager.DefaultManager
                .GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path;
            File.AppendAllText(Path.Combine(docs, "audio.log"), DateTime.Now.ToString("HH:mm:ss ") + s + "\n");
        }
        catch { }
    }

    public AudioOut(int rate, int ch, int bits)
    {
        _rate = rate; _ch = ch; _bits = bits; _frameBytes = ch * bits / 8;
        Log($"open {rate}Hz {ch}ch {bits}bit");
        _fmt = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, rate, (uint)ch, false);
        _engine.AttachNode(_node);
        _engine.Connect(_node, _engine.MainMixerNode, _fmt);
        lock (Live) Live.Add(this);
        if (_globalPaused) { _run.Reset(); return; }   // 백그라운드 중에 열린 경우: 돌아올 때까지 시작 안 함
        Start();
    }

    void Start()
    {
        lock (_startLock)
        {
            AVAudioSession.SharedInstance().SetActive(true, out _);
            _engine.Prepare();
            bool ok = _engine.StartAndReturnError(out NSError err);
            Log(ok ? "engine started" : "engine start FAILED: " + err?.LocalizedDescription);
            _node.Play();
        }
    }

    public long PlayedFrames => Interlocked.Read(ref _played);

    public void Write(ReadOnlySpan<byte> pcm)
    {
        while (!_disposed && !_run.Wait(100)) { }   // 백그라운드면 여기서 대기
        if (_disposed) return;
        int frames = pcm.Length / _frameBytes;
        if (frames == 0) return;
        if (!_engine.Running) { Log("engine not running, restarting"); Start(); }

        var buf = new AVAudioPcmBuffer(_fmt, (uint)frames) { FrameLength = (uint)frames };
        float** chans = (float**)buf.FloatChannelData;
        float peak = 0;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < _ch; c++)
            {
                int o = (f * _ch + c) * (_bits / 8);
                float v = _bits == 16
                    ? (short)(pcm[o] | (pcm[o + 1] << 8)) / 32768f
                    : (pcm[o] - 128) / 128f;
                chans[c][f] = v;
                if (Math.Abs(v) > peak) peak = Math.Abs(v);
            }

        int n = Interlocked.Increment(ref _writes);
        if (n <= 5 || n % 500 == 0) Log($"write #{n}: {frames} frames, peak {peak:F3}");

        int gen = Volatile.Read(ref _gen);
        Interlocked.Add(ref _scheduled, frames);
        _node.ScheduleBuffer(buf, () =>
        {
            if (gen == Volatile.Read(ref _gen)) Interlocked.Add(ref _played, frames);
        });

        // 안드로이드 AudioTrack 처럼, 재생 속도보다 앞서 쌓이면 기다린다 (최대 0.25초 앞까지)
        long lead = _rate / 4;
        var sw = Stopwatch.StartNew();
        while (!_disposed && Interlocked.Read(ref _scheduled) - Interlocked.Read(ref _played) > lead
               && sw.ElapsedMilliseconds < 500)
            Thread.Sleep(5);
    }

    public void Flush()
    {
        _node.Stop();
        Interlocked.Increment(ref _gen);
        Interlocked.Exchange(ref _played, Interlocked.Read(ref _scheduled));
        if (!_run.IsSet) return;   // 백그라운드 중에는 재생 재개하지 않음
        if (!_engine.Running) Start(); else _node.Play();
    }

    public void Dispose()
    {
        _disposed = true;
        _run.Set();
        lock (Live) Live.Remove(this);
        _node.Stop();
        _engine.Stop();
    }
}
