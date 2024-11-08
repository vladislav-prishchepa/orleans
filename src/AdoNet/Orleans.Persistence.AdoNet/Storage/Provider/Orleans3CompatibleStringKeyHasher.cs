using System;
using System.Buffers;
using System.Text;

namespace Orleans.Storage
{
    /// <summary>
    /// Orleans v3-compatible hasher implementation for string-only grain key ids.
    /// </summary>
    internal class Orleans3CompatibleStringKeyHasher : IHasher
    {
        private readonly Orleans3CompatibleHasher _innerHasher;
        private readonly string _grainType;

        public Orleans3CompatibleStringKeyHasher(Orleans3CompatibleHasher innerHasher, string grainType)
        {
            _innerHasher = innerHasher;
            _grainType = grainType;
        }

        /// <summary>
        /// <see cref="IHasher.Description"/>
        /// </summary>
        public string Description { get; } = $"Orleans v3 hash function ({nameof(JenkinsHash)}).";

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data)
        {
            // Orleans v3 treats string-only keys as integer keys with extension (AdoGrainKey.IsLongKey == true),
            // so data must be extended for string-only grain keys.
            // But AdoNetGrainStorage implementation also uses such code:
            //    ...
            //    var grainIdHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes());
            //    var grainTypeHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType));
            //    ...
            // PickHasher parameters are the same for both calls so we need to analyze data content to distinguish these cases.
            // It doesn't word if string key is equal to grain type name, but we consider this edge case to be negligibly rare.

            if (IsGrainTypeName(data))
                return _innerHasher.Hash(data);

            var extendedLength = data.Length + 8;

            var buffer = extendedLength switch
            {
                <= 32 => stackalloc byte[32],
                <= 64 => stackalloc byte[64],
                <= 128 => stackalloc byte[128],
                <= 256 => stackalloc byte[256],
                _ => Span<byte>.Empty
            };

            byte[] rentedBuffer = null;
            if (buffer.IsEmpty)
            {
                // assuming code below never throws, so calling ArrayPool.Return without try/finally block for JIT optimization
                rentedBuffer = ArrayPool<byte>.Shared.Rent(extendedLength);
                buffer = rentedBuffer.AsSpan();
            }

            data.AsSpan().CopyTo(buffer);
            // buffer may contain arbitrary data, setting zeros in 'extension' segment
            buffer.Slice(data.Length, 8).Clear();

            var hash = _innerHasher.Hash(buffer[..extendedLength]);

            if (rentedBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);

            return hash;
        }

        private bool IsGrainTypeName(byte[] data)
        {
            // at least 1 byte per char
            if (data.Length < _grainType.Length)
                return false;

            var grainTypeByteCount = Encoding.UTF8.GetByteCount(_grainType);
            if (grainTypeByteCount != data.Length)
                return false;

            var buffer = grainTypeByteCount switch
            {
                <= 32 => stackalloc byte[32],
                <= 64 => stackalloc byte[64],
                <= 128 => stackalloc byte[128],
                <= 256 => stackalloc byte[256],
                _ => Span<byte>.Empty
            };

            byte[] rentedBuffer = null;
            if (buffer.IsEmpty)
            {
                // assuming code below never throws, so calling ArrayPool.Return without try/finally block for JIT optimization
                rentedBuffer = ArrayPool<byte>.Shared.Rent(grainTypeByteCount);
                buffer = rentedBuffer.AsSpan();
            }

            var bytesWritten = Encoding.UTF8.GetBytes(_grainType, buffer);
            var isGrainType = buffer[..bytesWritten].SequenceEqual(data);
            if (rentedBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);

            return isGrainType;
        }
    }
}
