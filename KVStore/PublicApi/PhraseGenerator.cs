using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>Generates random word phrases from the EFF Short Wordlist #1 (1296 words, ~10.3 bits per word) using a cryptographically secure random number generator.</para>
	/// <para>Stateless: nothing is recorded or reserved.</para>
	/// <para>The word list is by the Electronic Frontier Foundation (https://www.eff.org/dice), licensed under CC BY 3.0 US.</para>
	/// </summary>
	public static class PhraseGenerator
	{
		/// <summary>
		/// Default number of words in a phrase.
		/// </summary>
		public const int DefaultWords = 6;
		/// <summary>
		/// Minimum number of words in a phrase.
		/// </summary>
		public const int MinWords = 5;
		/// <summary>
		/// Maximum number of words in a phrase.
		/// </summary>
		public const int MaxWords = 10;
		private static readonly Lazy<string[]> words = new Lazy<string[]>(LoadWords);
		/// <summary>
		/// Gets the word list.
		/// </summary>
		public static IReadOnlyList<string> Words => words.Value;
		private static string[] LoadWords()
		{
			Assembly assembly = typeof(PhraseGenerator).Assembly;
			string resourceName = assembly.GetManifestResourceNames().First(n => n.EndsWith("eff_short_wordlist_1.txt", StringComparison.Ordinal));
			List<string> list = new List<string>();
			using (Stream s = assembly.GetManifestResourceStream(resourceName))
			using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
			{
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					// Each line is "<dice digits>\t<word>".
					string[] parts = line.Split('\t');
					string word = parts[parts.Length - 1].Trim();
					if (word.Length > 0)
						list.Add(word);
				}
			}
			if (list.Count != 1296)
				throw new Exception("The EFF short word list should contain 1296 words, but contained " + list.Count + ".");
			return list.ToArray();
		}
		/// <summary>
		/// Returns a phrase of randomly chosen words joined by hyphens.
		/// </summary>
		/// <param name="wordCount">Number of words, between <see cref="MinWords"/> and <see cref="MaxWords"/>.</param>
		/// <returns></returns>
		public static string Generate(int wordCount)
		{
			if (wordCount < MinWords || wordCount > MaxWords)
				throw new ArgumentOutOfRangeException(nameof(wordCount));
			string[] list = words.Value;
			string[] chosen = new string[wordCount];
			for (int i = 0; i < wordCount; i++)
				chosen[i] = list[RandomNumberGenerator.GetInt32(list.Length)];
			return string.Join("-", chosen);
		}
	}
}
