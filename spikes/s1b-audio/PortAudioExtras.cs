using System;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace s1b_audio;

/// <summary>
/// PortAudioSharp2 (1.0.6) doesn't expose Pa_GetHostApiInfo or
/// Pa_IsFormatSupported, both of which the plan's S1b method explicitly asks
/// for ("host API (WASAPI/WDM-KS...)" and "Probe Pa_IsFormatSupported for
/// 16 kHz mono float32", §1.5). These are standard exported symbols of the
/// same native `portaudio` shared library that PortAudioSharp2 already
/// P/Invokes into (and which its NuGet package bundles per-RID), so this
/// file adds the two missing declarations directly rather than pulling in a
/// second audio binding.
/// </summary>
internal static class PortAudioExtras
{
    private const string PortAudioDll = "portaudio";

    public enum HostApiTypeId
    {
        InDevelopment = 0,
        DirectSound = 1,
        MME = 2,
        ASIO = 3,
        SoundManager = 4,
        CoreAudio = 5,
        OSS = 7,
        ALSA = 8,
        AL = 9,
        BeOS = 10,
        WDMKS = 11,
        JACK = 12,
        WASAPI = 13,
        AudioScienceHPI = 14,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaHostApiInfo
    {
        public int structVersion;
        public HostApiTypeId type;
        public IntPtr name; // const char*
        public int deviceCount;
        public int defaultInputDevice;
        public int defaultOutputDevice;
    }

    [DllImport(PortAudioDll)]
    private static extern IntPtr Pa_GetHostApiInfo(int hostApi);

    [DllImport(PortAudioDll)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern ErrorCode Pa_IsFormatSupported(IntPtr inputParameters, IntPtr outputParameters, double sampleRate);

    /// <summary>Human-readable host API name ("Windows WASAPI", "Windows WDM-KS", ...) for a device's hostApi index.</summary>
    public static (string Name, HostApiTypeId Type) GetHostApiName(int hostApiIndex)
    {
        IntPtr ptr = Pa_GetHostApiInfo(hostApiIndex);
        if (ptr == IntPtr.Zero)
            return ("<unknown>", HostApiTypeId.InDevelopment);

        var info = Marshal.PtrToStructure<PaHostApiInfo>(ptr);
        string name = info.name != IntPtr.Zero ? (Marshal.PtrToStringUTF8(info.name) ?? "<unknown>") : "<unknown>";
        return (name, info.type);
    }

    /// <summary>True if the given device supports the given mono format/rate for input.</summary>
    public static bool IsInputFormatSupported(int device, int channelCount, SampleFormat format, double sampleRate)
    {
        var p = new StreamParameters
        {
            device = device,
            channelCount = channelCount,
            sampleFormat = format,
            // suggestedLatency=0 here (unlike the real trial code, which uses
            // info.defaultLowInputLatency) -- Pa_IsFormatSupported largely ignores
            // latency for format/rate support checks on most host APIs anyway.
            suggestedLatency = 0,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<StreamParameters>());
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            ErrorCode ec = Pa_IsFormatSupported(ptr, IntPtr.Zero, sampleRate);
            return ec == ErrorCode.NoError;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
