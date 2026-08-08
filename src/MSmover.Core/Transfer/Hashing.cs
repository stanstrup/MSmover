using System.IO.Hashing;
using System.Security.Cryptography;
using MSmover.Core.Config;

namespace MSmover.Core.Transfer;

public interface IIncrementalHasher : IDisposable
{
    void Append(ReadOnlySpan<byte> data);
    string Finish();
}

public static class Hashing
{
    public static IIncrementalHasher Create(HashKind kind) => kind switch
    {
        HashKind.XxHash64 => new XxHasher(),
        HashKind.Sha256 => new CryptoHasher(IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
        HashKind.Md5 => new CryptoHasher(IncrementalHash.CreateHash(HashAlgorithmName.MD5)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class XxHasher : IIncrementalHasher
    {
        private readonly XxHash64 _h = new();
        public void Append(ReadOnlySpan<byte> data) => _h.Append(data);
        public string Finish() => Convert.ToHexString(_h.GetCurrentHash()).ToLowerInvariant();
        public void Dispose() { }
    }

    private sealed class CryptoHasher : IIncrementalHasher
    {
        private readonly IncrementalHash _h;
        public CryptoHasher(IncrementalHash h) => _h = h;
        public void Append(ReadOnlySpan<byte> data) => _h.AppendData(data);
        public string Finish() => Convert.ToHexString(_h.GetHashAndReset()).ToLowerInvariant();
        public void Dispose() => _h.Dispose();
    }
}
