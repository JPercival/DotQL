﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

[assembly: Ancestry.QueryProcessor.Type.Module(typeof(Ancestry.QueryProcessor.Runtime.SystemModule), new[] { "System" })]

namespace Ancestry.QueryProcessor.Runtime
{
    public class SystemModule
    {
        public static readonly Boolean Boolean;
        public static readonly Int32 Int32;
        public static readonly Int64 Int64;
        public static readonly Char Char;
        public static readonly String String;
        // TODO: public static readonly Date Date;
        // TODO: public static readonly Time Time;
        public static readonly DateTime DateTime;
        public static readonly Name Name;
        public static readonly Double Double;
        public static readonly Guid GUID;
        public static readonly TimeSpan TimeSpan;
        public static readonly Version Version;

        public Storage.IRepository<ISet<ModuleTuple>> Modules;

        public Storage.IRepository<ISet<UsingTuple>> DefaultUsings;

        public static IList<T> ToList<T>(ISet<T> setValue)
        {
            // TODO: ensure that the items coming from the set are ordered so that this is deterministic
            return new ListEx<T>(setValue);
        }

        public static ISet<T> ToSet<T>(IList<T> listValue)
        {
            return new Set<T>(listValue);
        }

		//List functions - what's the analog for head and tail? A tuple with the item and the lists
		//Push, pop, enqueue, dequeue?
		public static IList<T> Append<T>(IList<T> list, T value)
		{
			list.Add(value);
			return list;
		}

		public static IList<T> Remove<T>(IList<T> list, T value)
		{
			list.Remove(value);
			return list;
		}

		public static IList<T> Reverse<T>(IList<T> list)
		{
			//TODO: ToListEx extension method. How do we handle internal hidden functions?
			return list.Reverse().ToList();
		}

		//TODO: Set functions Union, Difference, Intersection, Product?
		public static ISet<T> Union<T>(ISet<T> left, ISet<T> right)
		{
			left.UnionWith(right);
			return left;
		}

		public static ISet<T> Intersection<T>(ISet<T> left, ISet<T> right)
		{
			left.IntersectWith(right);
			return left;
		}

		public static ISet<T> Difference<T>(ISet<T> left, ISet<T> right)
		{
			left.ExceptWith(right);
			return left;
		}
		
		//DateTume functions
		//return UTC string of datetime
		public static string ToString(DateTime date)
		{
			return date.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
		}

		public static DateTime ToUTC(DateTime date)
		{
			return date.ToUniversalTime();
		}

		public static DateTime AddYears(DateTime start, int years)
		{
			return start.AddYears(years);
		}

        public static DateTime AddMonths(DateTime start, int months)
        {
            return start.AddMonths(months);
        }

        public static DateTime AddDays(DateTime start, double days)
        {
            return start.AddDays(days);
        }

		public static DateTime AddHours(DateTime start, double hours)
		{
			return start.AddHours(hours);
		}

		public static DateTime AddMinutes(DateTime start, double minutes)
		{
			return start.AddMinutes(minutes);
		}

		public static DateTime AddSeconds(DateTime start, double seconds)
		{
			return start.AddSeconds(seconds);
		}

		public static DateTime AddMilliseconds(DateTime start, long milliseconds)
		{
			return start.AddMilliseconds(milliseconds);
		}

		public static DateTime AddTicks(DateTime start, long ticks)
		{
			return start.AddTicks(ticks);
		}

		

        //Numeric functions

		//Double functions
        public static double Abs(double value)
        {
            return Math.Abs(value);
        }

        public static double Acos(double value)
        {
            return Math.Acos(value);
        }

        public static double Asin(double value)
        {
            return Math.Asin(value);
        }

        public static double Atan(double value)
        {
            return Math.Atan(value);
        }

        public static double Atan2(double x, double y)
        {
            return Math.Atan2(x, y);
        }

        public static double Ceiling(double value)
        {
            return Math.Ceiling(value);
        }

        public static double Cos(double value)
        {
            return Math.Cos(value);
        }

        public static double Cosh(double value)
        {
            return Math.Cosh(value);
        }

        public static double Exp(double value)
        {
            return Math.Exp(value);
        }       

        public static double Frac(double value)
        {
            return value - Math.Truncate(value);
        }

        public static double Floor(double value)
        {
            return Math.Floor(value);
        }

        public static double IEEERemainder(double x, double y)
        {
            return Math.IEEERemainder(x, y);
        }

        public static double Ln(double value)
        {
            return Math.Log(value);
        }

        public static double Log(double value, double newBase)
        {
            return Math.Log(value, newBase);
        }

        public static double Log10(double value)
        {
            return Math.Log10(value);
        }

        public static double Max(double x, double y)
        {
            return Math.Max(x, y);
        }

        public static double Min(double x, double y)
        {
            return Math.Min(x, y);
        }

        public static double Pow(double x, double y)
        {
            return Math.Pow(x, y);
        }

        public static double Round(double value, int decimals)
        {
            return Math.Round(value, decimals);
        }

        public static int Sign(double value)
        {
            return Math.Sign(value);
        }

        public static double Sin(double value)
        {
            return Math.Sin(value);
        }

        public static double Sinh(double value)
        {
            return Math.Sinh(value);
        }

        public static double Sqrt(double value)
        {
            return Math.Sqrt(value);
        }

        public static double Tan(double value)
        {
            return Math.Tan(value);
        }

        public static double Tanh(double value)
        {
            return Math.Tanh(value);
        }

        public static double Truncate(double value)
        {
            return Math.Truncate(value);
        }

		//Long functions
		public static long Abs(long value)
		{
			return Math.Abs(value);
		}

		public static long Max(long x, long y)
		{
			return Math.Max(x, y);
		}

		public static long Min(long x, long y)
		{
			return Math.Min(x, y);
		}

		public static int Sign(long value)
		{
			return Math.Sign(value);
		}

		//Int functions
		public static long BigMul(int x, int y)
		{
			return Math.BigMul(x, y);
		}

		public static long Factorial(int value)
		{
			var ex = 0.0;
			var x = (double)value;
			x = x + x + 1;
			if (x > 1)
			{
				x = (Math.Log(2.0 * Math.PI) + Math.Log(x / 2.0) * x - x
				- (1.0 - 7.0 / (30.0 * x * x)) / (6.0 * x)) / 2.0;
				x = x / Math.Log(10);
				ex = Math.Floor(x);
				x = Math.Pow(10, x - ex);
			}

			return (long)Math.Truncate(x * Math.Pow(10, ex));
		}

		public static int Abs(int value)
		{
			return Math.Abs(value);
		}

		public static int Max(int x, int y)
		{
			return Math.Max(x, y);
		}

		public static int Min(int x, int y)
		{
			return Math.Min(x, y);
		}

		public static int Sign(int value)
		{
			return Math.Sign(value);
		}

        //String functions
        public static string Uppercase(string value)
        {
            return value.ToUpper();
        }

        public static string Lowercase(string value)
        {
            return value.ToLower();
        }

        public static string Concat(IList<string> values, string separator)
        {
            return String.Join(separator, values);
        }

        public static IList<string> Split(string value, ISet<string> delimiters)
        {
            return new ListEx<string>(value.Split(delimiters.ToArray(), StringSplitOptions.RemoveEmptyEntries));
        }

        public static int Length(string value)
        {
            return value.Length;
        }

        public static string Slice(string value, int startIndex, int length)
        {
			if (startIndex >= value.Length)
				return "";

            return value.Substring(startIndex, length);
        }

		public static bool IsMatch(string value, string pattern)
		{
			return System.Text.RegularExpressions.Regex.Match(value, pattern).Success;
		}

		//public static object Matches(string value, string pattern)
		//{
		//	var matches = System.Text.RegularExpressions.Regex.Matches(value, pattern);
		//}

        public static string Normalize(string value)
        {
            return System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
        }

		public static int IndexOf(string value, string token)
		{
			return value.IndexOf(token);
		}

		public static int IndexOf(string value, string token, int startIndex)
		{
			return value.IndexOf(token, startIndex);
		}

		public static int IndexOf(string value, string token, int startIndex, int count)
		{
			return value.IndexOf(token, startIndex, count);
		}

		public static int LastIndexOf(string value, string token)
		{
			return value.LastIndexOf(token);
		}

		public static int LastIndexOf(string value, string token, int startIndex)
		{
			return value.LastIndexOf(token, startIndex);
		}

		public static int LastIndexOf(string value, string token, int startIndex, int count)
		{
			return value.LastIndexOf(token, startIndex, count);
		}

        public static IList<char> Explode(string value)
        {
            return value.ToCharArray();
        }

        public static string Implode(IList<char> chars)
        {
            return new string(chars.ToArray());
        }

		//ToString overloads -- Should these be grouped with the types?
		public static string ToString<T>(T value)
		{
			return value.ToString();
		}

		public static string ToString(char value)
		{
			return value.ToString();
		}

		public static string ToString(int value)
		{
			return value.ToString();
		}

		public static string ToString(long value)
		{
			return value.ToString();
		}

		public static string ToString(bool value)
		{
			return value.ToString();
		}

		public static string ToString(string value)
		{
			return value;
		}

		public static string ToString(DateTime value)
		{
			return value.ToUniversalTime().ToString();
		}
	}
}
