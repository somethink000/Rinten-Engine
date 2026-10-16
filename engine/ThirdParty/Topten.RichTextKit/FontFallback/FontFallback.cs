// RichTextKit
// Copyright © 2019-2020 Topten Software. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); you may 
// not use this product except in compliance with the License. You may obtain 
// a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
// License for the specific language governing permissions and limitations 
// under the License.

using SkiaSharp;
using System;
using System.Collections.Generic;
using Topten.RichTextKit.Utils;

namespace Topten.RichTextKit
{
	/// <summary>
	/// Helper to split a run of code points based on a particular typeface
	/// into a series of runs where unsupported code points are mapped to a
	/// fallback font.
	/// </summary>
	public class FontFallback
	{
		/// <summary>
		/// Specified details about a font fallback run
		/// </summary>
		public struct Run
		{
			/// <summary>
			/// The starting code point index of this run
			/// </summary>
			public int Start;

			/// <summary>
			/// The length of this run in code points
			/// </summary>
			public int Length;

			/// <summary>
			/// The typeface to be used for this run
			/// </summary>
			public SKTypeface Typeface;
		}

		/// <summary>
		/// Specifies the instance of the character matcher to be used for font fallback
		/// </summary>
		/// <remarks>
		/// This instance is shared by all TextBlock instances and should be thread safe
		/// if used in a multi-threaded environment.
		/// </remarks>
		public static ICharacterMatcher CharacterMatcher = new DefaultCharacterMatcher();

		/// <summary>
		/// Splits a sequence of code points into a series of runs with font fallback applied
		/// </summary>
		/// <param name="codePoints">The code points</param>
		/// <param name="typeface">The preferred typeface</param>
		/// <param name="replacementCharacter">The replacement character to be used for the run</param>
		/// <returns>A sequence of runs with unsupported code points replaced by a selected font fallback</returns>
		public static IEnumerable<Run> GetFontRuns( Slice<int> codePoints, SKTypeface typeface, char replacementCharacter = '\0' )
		{
			var runs = new List<Run>();
			GetFontRuns( codePoints, typeface, runs, replacementCharacter );
			return runs;
		}

		//Which face draws a code point this one has no glyph for, asked once a thread: the matcher reads the family's name and makes
		//a style for every question, and text that keeps a character its font lacks asked it again for every string
		[ThreadStatic] static Dictionary<(SKTypeface Typeface, int CodePoint), SKTypeface> _matches;

		static SKTypeface Match( SKTypeface typeface, int codePoint )
		{
			var matches = _matches ??= new Dictionary<(SKTypeface, int), SKTypeface>();

			if ( matches.TryGetValue( (typeface, codePoint), out var found ) )
				return found;

			//Let go and started again past a bound, as a text of every character would otherwise keep an answer for each
			if ( matches.Count >= 4096 ) matches.Clear();

			found = CharacterMatcher.MatchCharacter( typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant, null, codePoint );
			matches[(typeface, codePoint)] = found;
			return found;
		}

		//The glyphs of the run being split, one array a thread grown as needed - see GetFontRuns
		[ThreadStatic] static ushort[] _glyphs;

		//Any size: which glyph a code point is does not depend on it, and the cache keeps one font of it
		const float GlyphLookupSize = 12;

		/// <summary>The runs of a span that each one typeface draws, added to a list the caller keeps: nothing made per call.</summary>
		public static void GetFontRuns( Slice<int> codePoints, SKTypeface typeface, List<Run> into, char replacementCharacter = '\0' )
		{
			if ( replacementCharacter != '\0' )
			{
				Span<int> replacement = stackalloc int[1] { replacementCharacter };
				Span<ushort> replacementGlyph = stackalloc ushort[1];
				FontCache.Get( typeface, GlyphLookupSize ).GetGlyphs( replacement, replacementGlyph );

				if ( replacementGlyph[0] == 0 )
				{
					var fallbackTypeface = CharacterMatcher.MatchCharacter( typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant, null, replacementCharacter );
					if ( fallbackTypeface != null )
						typeface = fallbackTypeface;
				}

				into.Add( new Run() { Start = 0, Length = codePoints.Length, Typeface = typeface } );
				return;
			}

			// Get glyphs using the top-level typeface
			if ( _glyphs == null || _glyphs.Length < codePoints.Length )
				_glyphs = new ushort[Math.Max( codePoints.Length, 64 ) * 2];

			var glyphs = _glyphs;
			FontCache.Get( typeface, GlyphLookupSize ).GetGlyphs( codePoints.AsSpan(), new Span<ushort>( glyphs, 0, codePoints.Length ) );

			// Look for subspans that need font fallback (where glyphs are zero)
			int runStart = 0;
			for ( int i = 0; i < codePoints.Length; i++ )
			{
				// Do we need fallback for this character?
				if ( glyphs[i] == 0 )
				{
					// Don't fallback for whitespace characters
					if ( UnicodeClasses.BoundaryGroup( codePoints[i] ) == WordBoundaryClass.Space )
						continue;

					// Must be a cluster boundary
					if ( !GraphemeClusterAlgorithm.IsBoundary( codePoints, i ) )
						continue;

					// Check if there's a fallback available, if not, might as well continue with the current top-level typeface
					var subSpanTypeface = Match( typeface, codePoints[i] );
					if ( subSpanTypeface == null )
						continue;

					// We can do font fallback...

					// Flush the current top-level run
					if ( i > runStart )
						into.Add( new Run() { Start = runStart, Length = i - runStart, Typeface = typeface } );

					// Count how many unmatched characters
					var unmatchedStart = i;
					var unmatchedEnd = i + 1;
					while ( unmatchedEnd < codePoints.Length &&
							(glyphs[unmatchedEnd] == 0 || !GraphemeClusterAlgorithm.IsBoundary( codePoints, unmatchedEnd )) )
					{
						unmatchedEnd++;
					}
					var unmatchedLength = unmatchedEnd - unmatchedStart;

					// Match the missing characters
					while ( unmatchedLength > 0 )
					{
						// Find the font fallback using the first character
						subSpanTypeface = Match( typeface, codePoints[unmatchedStart] );
						if ( subSpanTypeface == null )
						{
							unmatchedEnd = unmatchedStart;
							break;
						}

						// Get the glyphs over the current unmatched range
						FontCache.Get( subSpanTypeface, GlyphLookupSize ).GetGlyphs( codePoints.SubSlice( unmatchedStart, unmatchedLength ).AsSpan(), new Span<ushort>( glyphs, unmatchedStart, unmatchedLength ) );

						// Count how many characters were matched
						var fallbackStart = unmatchedStart;
						var fallbackEnd = unmatchedStart + 1;
						while ( fallbackEnd < unmatchedEnd && glyphs[fallbackEnd] != 0 )
							fallbackEnd++;
						var fallbackLength = fallbackEnd - fallbackStart;

						into.Add( new Run() { Start = fallbackStart, Length = fallbackLength, Typeface = subSpanTypeface } );

						// Continue selecting font fallbacks until the entire unmatched ranges has been matched
						unmatchedStart += fallbackLength;
						unmatchedLength -= fallbackLength;
					}

					// Move onto the next top level span
					i = unmatchedEnd - 1;           // account for i++ on for loop
					runStart = unmatchedEnd;
				}
			}

			// Flush find run
			if ( codePoints.Length > runStart )
				into.Add( new Run() { Start = runStart, Length = codePoints.Length - runStart, Typeface = typeface } );
		}
	}
}
