using System.Collections;

namespace DomainCopilot.Application;

public static class ApplicationValueEquality
{
    public static bool SequenceEqual<T>(IEnumerable<T> left, IEnumerable<T> right)
    {
        using var a = left.GetEnumerator(); using var b = right.GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext(); var hasB = b.MoveNext();
            if (hasA != hasB) return false;
            if (!hasA) return true;
            if (!ValueEquals(a.Current, b.Current)) return false;
        }
    }

    public static int SequenceHash<T>(IEnumerable<T> values)
    { var hash = new HashCode(); foreach (var item in values) hash.Add(ValueHash(item)); return hash.ToHashCode(); }

    public static bool DictionaryEqual(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left) if (!right.TryGetValue(pair.Key, out var value) || !ValueEquals(pair.Value, value)) return false;
        return true;
    }

    public static int DictionaryHash(IReadOnlyDictionary<string, object?> value)
    { var hash = 0; foreach (var pair in value) hash ^= HashCode.Combine(pair.Key, ValueHash(pair.Value)); return HashCode.Combine(value.Count, hash); }

    private static bool ValueEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.GetType() != right.GetType()) return false;
        if (left is IReadOnlyDictionary<string, object?> leftMap && right is IReadOnlyDictionary<string, object?> rightMap) return DictionaryEqual(leftMap, rightMap);
        if (left is IDictionary leftDict && right is IDictionary rightDict)
        {
            if (leftDict.Count != rightDict.Count) return false;
            foreach (DictionaryEntry entry in leftDict)
                if (!rightDict.Contains(entry.Key) || !ValueEquals(entry.Value, rightDict[entry.Key])) return false;
            return true;
        }
        if (left is IEnumerable a && right is IEnumerable b && left is not string)
        {
            var ea = a.GetEnumerator(); var eb = b.GetEnumerator();
            try
            {
                while (true) { var hasA = ea.MoveNext(); var hasB = eb.MoveNext(); if (hasA != hasB) return false; if (!hasA) return true; if (!ValueEquals(ea.Current, eb.Current)) return false; }
            }
            finally { (ea as IDisposable)?.Dispose(); (eb as IDisposable)?.Dispose(); }
        }
        return left.Equals(right);
    }

    private static int ValueHash(object? value)
    {
        if (value is null) return 0;
        if (value is IReadOnlyDictionary<string, object?> map) return DictionaryHash(map);
        if (value is IDictionary dictionary)
        { var result = 0; foreach (DictionaryEntry pair in dictionary) result ^= HashCode.Combine(ValueHash(pair.Key), ValueHash(pair.Value)); return HashCode.Combine(dictionary.Count, result); }
        if (value is IEnumerable values && value is not string)
        { var hash = new HashCode(); foreach (var item in values) hash.Add(ValueHash(item)); return hash.ToHashCode(); }
        return value.GetHashCode();
    }
}
