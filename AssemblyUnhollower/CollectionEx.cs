using System;
using System.Collections.Generic;

namespace AssemblyUnhollower
{
    public static class CollectionEx
    {
        public static TV GetOrCreate<TK, TV>(this IDictionary<TK, TV> dict, TK key, Func<TK, TV> valueFactory) where TK : notnull
        {
            if (!dict.TryGetValue(key, out var result))
            {
                result = valueFactory(key);
                dict[key] = result;
            }

            return result;
        }
    }
}