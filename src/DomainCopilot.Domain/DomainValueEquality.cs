using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace DomainCopilot.Domain;

/// <summary>Helpers for Python-like structural value and tuple equality.</summary>
internal static class DomainValueEquality
{
    private enum SequenceKind { List, Array, ImmutableArray }

    public static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values) =>
        values.IsDefault ? ImmutableArray<T>.Empty : values;

    public static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.Length != right.Length) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < left.Length; index++)
            if (!comparer.Equals(left[index], right[index])) return false;
        return true;
    }

    public static int SequenceHash<T>(ImmutableArray<T> values)
    {
        var hash = new HashCode();
        hash.Add(values.Length);
        foreach (var value in values) hash.Add(value);
        return hash.ToHashCode();
    }

    public static bool DictionaryEqual(
        IDictionary<string, object?>? left,
        IDictionary<string, object?>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            var found = false;
            foreach (var candidate in right)
            {
                if (!string.Equals(pair.Key, candidate.Key, StringComparison.Ordinal)) continue;
                if (!ValueEqual(pair.Value, candidate.Value)) return false;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    public static int DictionaryHash(IDictionary<string, object?>? values)
    {
        if (values is null) return 0;
        var sum = 0;
        foreach (var pair in values)
            sum = unchecked(sum + (StringComparer.Ordinal.GetHashCode(pair.Key) ^ ValueHash(pair.Value)));
        return HashCode.Combine(values.Count, sum);
    }

    private static bool ValueEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (IsNumber(left) && IsNumber(right)) return NumbersEqual(left, right);

        if (left is IDictionary leftMap && right is IDictionary rightMap)
        {
            if (leftMap.Count != rightMap.Count) return false;
            foreach (DictionaryEntry entry in leftMap)
            {
                var found = false;
                foreach (DictionaryEntry candidate in rightMap)
                {
                    if (!ValueEqual(entry.Key, candidate.Key)) continue;
                    if (!ValueEqual(entry.Value, candidate.Value)) return false;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        var leftIsSequence = TryGetSequence(left, out var leftKind, out var leftSequence, out var leftShape);
        var rightIsSequence = TryGetSequence(right, out var rightKind, out var rightSequence, out var rightShape);
        if (leftIsSequence || rightIsSequence)
        {
            if (!leftIsSequence || !rightIsSequence || leftKind != rightKind ||
                !leftShape.SequenceEqual(rightShape)) return false;

            var leftEnumerator = leftSequence.GetEnumerator();
            var rightEnumerator = rightSequence.GetEnumerator();
            while (leftEnumerator.MoveNext())
            {
                if (!rightEnumerator.MoveNext() || !ValueEqual(leftEnumerator.Current, rightEnumerator.Current))
                    return false;
            }
            return !rightEnumerator.MoveNext();
        }
        return left.Equals(right);
    }

    private static int ValueHash(object? value)
    {
        if (value is null) return 0;
        if (IsNumber(value)) return Convert.ToDouble(value, CultureInfo.InvariantCulture).GetHashCode();
        if (value is IDictionary map)
        {
            var sum = 0;
            foreach (DictionaryEntry entry in map)
                sum = unchecked(sum + (ValueHash(entry.Key) ^ ValueHash(entry.Value)));
            return HashCode.Combine(map.Count, sum);
        }
        if (TryGetSequence(value, out var kind, out var sequence, out var shape))
        {
            var hash = new HashCode();
            hash.Add(kind);
            foreach (var dimension in shape) hash.Add(dimension);
            foreach (var item in sequence) hash.Add(ValueHash(item));
            return hash.ToHashCode();
        }
        return value.GetHashCode();
    }

    private static bool TryGetSequence(
        object value,
        out SequenceKind kind,
        out IEnumerable sequence,
        out int[] shape)
    {
        if (value is Array array)
        {
            kind = SequenceKind.Array;
            sequence = array;
            shape = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
            return true;
        }

        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            kind = SequenceKind.ImmutableArray;
            var isDefault = (bool)type.GetProperty(nameof(ImmutableArray<int>.IsDefault))!.GetValue(value)!;
            var length = (int)type.GetProperty(nameof(ImmutableArray<int>.Length))!.GetValue(value)!;
            sequence = isDefault ? Array.Empty<object>() : (IEnumerable)value;
            shape = [length];
            return true;
        }

        if (value is IList list)
        {
            kind = SequenceKind.List;
            sequence = list;
            shape = [list.Count];
            return true;
        }

        kind = default;
        sequence = Array.Empty<object>();
        shape = [];
        return false;
    }

    private static bool IsNumber(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or bool;

    private static bool NumbersEqual(object left, object right)
    {
        if (IsIntegral(left) && IsIntegral(right)) return ToBigInteger(left) == ToBigInteger(right);
        if (left is float or double || right is float or double)
        {
            var floating = left is float or double ? left : right;
            var other = ReferenceEquals(floating, left) ? right : left;
            var floatingValue = Convert.ToDouble(floating, CultureInfo.InvariantCulture);
            if (IsIntegral(other))
            {
                return double.IsFinite(floatingValue) && floatingValue == Math.Truncate(floatingValue) &&
                    new BigInteger(floatingValue) == ToBigInteger(other);
            }
            return floatingValue.Equals(Convert.ToDouble(other, CultureInfo.InvariantCulture));
        }
        return Convert.ToDecimal(left, CultureInfo.InvariantCulture) ==
            Convert.ToDecimal(right, CultureInfo.InvariantCulture);
    }

    private static bool IsIntegral(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or bool;

    private static BigInteger ToBigInteger(object value) => value switch
    {
        bool boolean => boolean ? BigInteger.One : BigInteger.Zero,
        _ => BigInteger.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
    };
}
