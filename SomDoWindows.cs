using System.Runtime.InteropServices;

namespace Pdv;

/// <summary>
/// O volume do Windows pelo Core Audio (1.0.58, barra do Windows no caixa): a saída padrão, a mesma
/// que o ícone do alto-falante da barra de tarefas mexe. Não depende do Explorer. Nada aqui lança.
/// </summary>
public static class SomDoWindows
{
    public sealed record Leitura(bool Achou, int Porcentagem, bool Mudo);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }
    // a ordem da vtable é a do endpointvolume.h; só os do meio são usados
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint canais);
        int SetMasterVolumeLevel(float db, ref Guid contexto);
        int SetMasterVolumeLevelScalar(float nivel, ref Guid contexto);
        int GetMasterVolumeLevel(out float db);
        int GetMasterVolumeLevelScalar(out float nivel);
        int SetChannelVolumeLevel(uint canal, float db, ref Guid contexto);
        int SetChannelVolumeLevelScalar(uint canal, float nivel, ref Guid contexto);
        int GetChannelVolumeLevel(uint canal, out float db);
        int GetChannelVolumeLevelScalar(uint canal, out float nivel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mudo, ref Guid contexto);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mudo);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    private const int ClsctxAll = 23;

    /// <summary>Abre o volume da saída padrão, roda <paramref name="acao"/> e solta o COM.</summary>
    private static T ComVolume<T>(Func<IAudioEndpointVolume?, T> acao)
    {
        object? en = null, vol = null;
        IMMDevice? dev = null;
        try
        {
            en = new MMDeviceEnumerator();
            if (((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev is null) return acao(null);   // eRender, eMultimedia
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, ClsctxAll, IntPtr.Zero, out vol) != 0 || vol is null) return acao(null);
            return acao((IAudioEndpointVolume)vol);
        }
        finally
        {
            try { if (vol is not null) Marshal.ReleaseComObject(vol); } catch { }
            try { if (dev is not null) Marshal.ReleaseComObject(dev); } catch { }
            try { if (en is not null) Marshal.ReleaseComObject(en); } catch { }
        }
    }

    public static Leitura Ler()
    {
        try
        {
            return ComVolume(v =>
            {
                if (v is null || v.GetMasterVolumeLevelScalar(out var nivel) != 0) return new Leitura(false, 0, false);
                v.GetMute(out var mudo);
                return new Leitura(true, Nucleo.BarraDoWindows.Porcentagem(nivel), mudo);
            });
        }
        catch { return new Leitura(false, 0, false); }
    }

    /// <summary>Muda o volume (0 a 100). Subir o volume tira o mudo, como no Windows.</summary>
    public static bool Volume(int porcentagem)
    {
        try
        {
            return ComVolume(v =>
            {
                if (v is null) return false;
                var ctx = Guid.Empty;
                var ok = v.SetMasterVolumeLevelScalar(Nucleo.BarraDoWindows.Escalar(porcentagem), ref ctx) == 0;
                if (ok && porcentagem > 0) v.SetMute(false, ref ctx);
                return ok;
            });
        }
        catch { return false; }
    }

    public static bool Mudo(bool mudo)
    {
        try
        {
            return ComVolume(v =>
            {
                if (v is null) return false;
                var ctx = Guid.Empty;
                return v.SetMute(mudo, ref ctx) == 0;
            });
        }
        catch { return false; }
    }
}
