using System.Diagnostics.CodeAnalysis;

namespace Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// A read-only, allocation-free view of a 513-slot universe array (index = address, slot 0 unused)
/// as the channel dictionary <see cref="DmxFixtureColorMapper"/> reads. Lets every fixture card on
/// the desk read the same buffer without building a 512-entry dictionary per card per render.
/// </summary>
public sealed class DmxUniverseView(byte[] values) : IReadOnlyDictionary<int, byte>
{
    public const int UniverseSize = 512;

    public byte this[int key] => key is >= 1 and <= UniverseSize ? values[key] : throw new KeyNotFoundException();

    public IEnumerable<int> Keys => Enumerable.Range(1, UniverseSize);

    public IEnumerable<byte> Values => values.Skip(1).Take(UniverseSize);

    public int Count => UniverseSize;

    public bool ContainsKey(int key) => key is >= 1 and <= UniverseSize;

    public bool TryGetValue(int key, [MaybeNullWhen(false)] out byte value)
    {
        if (ContainsKey(key))
        {
            value = values[key];
            return true;
        }

        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<int, byte>> GetEnumerator()
    {
        for (int address = 1; address <= UniverseSize; address++)
        {
            yield return new(address, values[address]);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
