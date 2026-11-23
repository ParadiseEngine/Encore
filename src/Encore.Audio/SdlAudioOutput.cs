using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;

namespace Encore.Audio;

/// <summary>
/// The game's sound device, which each host opens and owns: SDL3's default playback device at 48 kHz
/// stereo float, whose audio callback renders the <see cref="AudioMixer"/>. Only that callback
/// advances the mix. On the native host SDL calls it on its own thread, so it keeps playing while the
/// render thread is busy and the music goes on through a level's loading. In a browser it plays at the
/// page's rate (SDL resamples) and is called on the page's main thread, between frames, so a frame longer
/// than the device's buffer is a dropout; the page's audio only starts once the player has interacted with it.
/// </summary>
public sealed unsafe class SdlAudioOutput : IDisposable
{
    private const int BufferFrames = 1024;
    private const int BytesPerFrame = AudioMixer.Channels * sizeof(float);
    private readonly AudioMixer _mixer;
    private readonly float[] _buffer = new float[BufferFrames * AudioMixer.Channels];
    private SDL_AudioStream* _stream;
    private GCHandle _self;
    private bool _initialized;
    private bool _disposed;
    private Exception? _failure;

    /// <summary>Opens the device and starts rendering <paramref name="mixer"/>, which the callback then owns.</summary>
    public SdlAudioOutput(AudioMixer mixer)
    {
        _mixer = mixer;
        try
        {
            if (!SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO)) throw SdlError("SDL_InitSubSystem(AUDIO)");
            _initialized = true;
            _self = GCHandle.Alloc(this);
            var spec = new SDL_AudioSpec
            {
                format = SDL3.SDL_AUDIO_F32,
                channels = AudioMixer.Channels,
                freq = AudioMixer.SampleRate,
            };
            _stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &SupplyAudio, GCHandle.ToIntPtr(_self));
            if (_stream == null) throw SdlError("SDL_OpenAudioDeviceStream");
            if (!SDL3.SDL_ResumeAudioStreamDevice(_stream)) throw SdlError("SDL_ResumeAudioStreamDevice");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Seconds of sound the device has pulled from the mixer so far.</summary>
    public double Seconds => _mixer.RenderedFrames / (double)AudioMixer.SampleRate;

    /// <summary>Rethrows what stopped the mix, if anything did.</summary>
    public void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _failure) is { } failure)
            throw new InvalidOperationException("Sound playback failed.", failure);
    }

    /// <summary>
    /// Holds the device (silence, the mix standing still) or lets it play on: a page holds it whenever its
    /// frames, and so the game, do not run (until its first frame, and while it is hidden).
    /// </summary>
    public void Pause(bool paused)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!(paused ? SDL3.SDL_PauseAudioStreamDevice(_stream) : SDL3.SDL_ResumeAudioStreamDevice(_stream)))
            throw SdlError(paused ? "SDL_PauseAudioStreamDevice" : "SDL_ResumeAudioStreamDevice");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SupplyAudio(nint userdata, SDL_AudioStream* stream, int additionalBytes, int totalBytes)
    {
        var output = (SdlAudioOutput)GCHandle.FromIntPtr(userdata).Target!;
        if (additionalBytes <= 0 || Volatile.Read(ref output._failure) is not null) return;
        try
        {
            // SDL asks in input-format bytes. Round up to complete stereo frames, never queue ahead of demand.
            var remaining = (additionalBytes + (long)BytesPerFrame - 1) / BytesPerFrame;
            while (remaining > 0)
            {
                var frames = (int)Math.Min(remaining, BufferFrames);
                var samples = output._buffer.AsSpan(0, frames * AudioMixer.Channels);
                output._mixer.Render(samples);
                fixed (float* data = samples)
                    if (!SDL3.SDL_PutAudioStreamData(stream, (nint)data, samples.Length * sizeof(float)))
                        throw SdlError("SDL_PutAudioStreamData");
                remaining -= frames;
            }
        }
        catch (Exception error)
        {
            // Exceptions must never cross the unmanaged callback boundary; the main thread reports the failure.
            Volatile.Write(ref output._failure, error);
        }
    }

    private static InvalidOperationException SdlError(string operation) => new($"{operation}: {SDL3.SDL_GetError()}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Destroy waits for in-flight callbacks; keep userdata alive until the stream can no longer use it.
        if (_stream != null)
        {
            SDL3.SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
        if (_self.IsAllocated) _self.Free();
        if (_initialized)
        {
            SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            _initialized = false;
        }
    }
}
