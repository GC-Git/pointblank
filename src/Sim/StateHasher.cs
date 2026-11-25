namespace PointBlank.Sim;

/// <summary>
/// 64-bit FNV-1a over a canonical byte serialisation: multi-byte values are fed least
/// significant byte first, whatever the machine's own byte order. Stable across runs,
/// processes and machines. It detects divergence; it is not a security hash.
/// </summary>
public sealed class StateHasher
{
    private const ulong OffsetBasis = 0xcbf29ce484222325UL;
    private const ulong Prime = 0x00000100000001b3UL;

    public ulong Value { get; private set; } = OffsetBasis;

    public void Add(byte value) => Value = unchecked((Value ^ value) * Prime);

    public void Add(bool value) => Add(value ? (byte)1 : (byte)0);

    public void Add(int value) => AddLittleEndian(unchecked((uint)value), sizeof(int));

    public void Add(long value) => AddLittleEndian(unchecked((ulong)value), sizeof(long));

    public void Add(ulong value) => AddLittleEndian(value, sizeof(ulong));

    public void Add(Fixed value) => Add(value.Raw);

    private void AddLittleEndian(ulong value, int byteCount)
    {
        for (int i = 0; i < byteCount; i++)
        {
            Add(unchecked((byte)(value >> (8 * i))));
        }
    }
}
