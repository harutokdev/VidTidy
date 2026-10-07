using System.Text.Json;

namespace ThroughputLab;

internal static class Json
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
