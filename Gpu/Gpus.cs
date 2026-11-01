using Vortice.DXGI;

namespace ShaderLab.Gpu;

/// <summary>
/// GPU-valg. Indeksene er DXGI sin EnumAdapters1-rekkefølge, den samme FFmpeg bruker for D3D11VA.
/// Uten oppgitt enhet tar FFmpeg adapter 0 (den som driver hovedskjermen, ofte iGPU-en), så "auto"
/// velger i stedet den Windows regner som høyytelses-GPU.
/// </summary>
static class Gpus
{
    private static List<AdapterDescription1> All()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var list = new List<AdapterDescription1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success; i++)
            using (a) list.Add(a!.Description1);
        return list;
    }

    private static int Auto(List<AdapterDescription1> all)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? a).Success; i++)
            using (a)
            {
                var d = a!.Description1;
                int index = all.FindIndex(x => x.Luid.Equals(d.Luid));
                if (index >= 0 && !d.Flags.HasFlag(AdapterFlags.Software)) return index;
            }
        return 0;
    }

    public static void Print()
    {
        var all = All();
        int auto = Auto(all);
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            string notes = (d.Flags.HasFlag(AdapterFlags.Software) ? ", software" : "") + (i == auto ? "  <- auto" : "");
            Console.WriteLine($"{i}: {d.Description} ({d.DedicatedVideoMemory / (1024 * 1024)} MB VRAM{notes})");
        }
    }

    /// <summary>"auto" eller et tall fra --list-gpus. Skriver ut hvilken GPU som brukes; kaster ArgumentException ved ugyldig valg.</summary>
    public static int Select(string choice)
    {
        var all = All();
        int index;
        if (choice.Equals("auto", StringComparison.OrdinalIgnoreCase))
            index = Auto(all);
        else if (!int.TryParse(choice, out index) || index < 0 || index >= all.Count)
            throw new ArgumentException($"Ugyldig --gpu '{choice}': bruk auto eller 0-{all.Count - 1} (se --list-gpus)");
        else if (all[index].Flags.HasFlag(AdapterFlags.Software))
            throw new ArgumentException($"GPU {index} ({all[index].Description}) er en software-adapter uten videodekoder");
        Console.WriteLine($"GPU {index}: {all[index].Description}");
        return index;
    }
}
