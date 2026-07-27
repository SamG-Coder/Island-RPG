namespace IslandRpg.World;

internal static class BiomeWeightBlender
{
    private const int KernelRadius = 10;
    private const float KernelSigma = 4.6f;

    public static (byte[] A, byte[] B, byte[] C, byte[] D) Build(
        byte[] labels,
        int size,
        CancellationToken cancellationToken = default)
    {
        var materialCount = Enum.GetValues<Biome>().Length;
        var usedMaterials = new bool[materialCount];
        foreach (var label in labels)
            usedMaterials[label] = true;
        var activeMaterials = Enumerable.Range(0, materialCount)
            .Where(index => usedMaterials[index])
            .ToArray();
        var activeLookup = new int[materialCount];
        for (var channel = 0; channel < activeMaterials.Length; channel++)
            activeLookup[activeMaterials[channel]] = channel;

        var channels = activeMaterials.Length;
        var weights = new float[size * size * channels];
        for (var pixel = 0; pixel < labels.Length; pixel++)
            weights[pixel * channels + activeLookup[labels[pixel]]] = 1;

        var kernel = BuildKernel();
        var scratch = new float[weights.Length];
        for (var y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            for (var channel = 0; channel < channels; channel++)
            {
                var value = 0f;
                for (var offset = -KernelRadius;
                     offset <= KernelRadius;
                     offset++)
                    value += weights[
                        (y * size +
                         Math.Clamp(x + offset, 0, size - 1)) *
                        channels + channel] *
                        kernel[offset + KernelRadius];
                scratch[(y * size + x) * channels + channel] = value;
            }
        }

        for (var y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            for (var channel = 0; channel < channels; channel++)
            {
                var value = 0f;
                for (var offset = -KernelRadius;
                     offset <= KernelRadius;
                     offset++)
                    value += scratch[
                        (Math.Clamp(y + offset, 0, size - 1) *
                         size + x) * channels + channel] *
                        kernel[offset + KernelRadius];
                weights[(y * size + x) * channels + channel] = value;
            }
        }

        var a = new byte[size * size * 4];
        var b = new byte[size * size * 4];
        var c = new byte[size * size * 4];
        var d = new byte[size * size * 4];
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
                total += weights[pixel * channels + channel];
            for (var channel = 0; channel < channels; channel++)
            {
                var value = (byte)Math.Clamp(
                    MathF.Round(
                        weights[pixel * channels + channel] /
                        Math.Max(total, .0001f) * 255),
                    0,
                    255);
                var material = activeMaterials[channel];
                var target = material switch
                {
                    < 4 => a,
                    < 8 => b,
                    < 12 => c,
                    _ => d
                };
                target[pixel * 4 + material % 4] = value;
            }
        }
        return (a, b, c, d);
    }

    private static float[] BuildKernel()
    {
        var kernel = new float[KernelRadius * 2 + 1];
        var total = 0f;
        for (var offset = -KernelRadius;
             offset <= KernelRadius;
             offset++)
        {
            var value = MathF.Exp(
                -(offset * offset) /
                (2f * KernelSigma * KernelSigma));
            kernel[offset + KernelRadius] = value;
            total += value;
        }
        for (var index = 0; index < kernel.Length; index++)
            kernel[index] /= total;
        return kernel;
    }
}
