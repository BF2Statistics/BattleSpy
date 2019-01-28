using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BattlelogMaster
{
    public static class StringExtensions
    {
        public static string TruncateAtWord(this string input, int length, string suffix = "...")
        {
            if (input == null || input.Length < length)
                return input;

            length = length - suffix.Length;
            int iNextSpace = input.LastIndexOf(" ", length, StringComparison.Ordinal);
            return String.Format("{0}" + suffix, input.Substring(0, (iNextSpace > 0) ? iNextSpace : length).Trim());
        }

        public static string Truncate(this string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
