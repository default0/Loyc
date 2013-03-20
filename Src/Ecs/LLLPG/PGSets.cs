﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Loyc.CompilerCore;
using System.Diagnostics;
using Loyc.Collections.Impl;
using Loyc.Threading;
using Loyc.Utilities;
using S = ecs.CodeSymbols;

namespace Loyc.LLParserGenerator
{
	/// <summary>This interface represents a set of terminals (and <i>only</i> a 
	/// set of terminals, unlike <see cref="TerminalPred"/> which includes actions 
	/// and a Basis Node). Typical parsers and lexers only need one implementation:
	/// <see cref="PGIntSet"/>.</summary>
	/// </summary>
	public interface IPGTerminalSet : IEquatable<IPGTerminalSet>
	{
		/// <summary>Merges two sets.</summary>
		/// <returns>The combination of the two sets, or null if other's type is not supported.</returns>
		IPGTerminalSet UnionCore(IPGTerminalSet other);
		/// <summary>Computes the intersection of two sets.</summary>
		/// <returns>A set that has only items that are in both sets, or null if other's type is not supported.</returns>
		IPGTerminalSet IntersectionCore(IPGTerminalSet other, bool subtract = false, bool subtractThis = false);

		bool IsInverted { get; }
		bool ContainsEOF { get; }
		bool IsEmptySet { get; }
		bool ContainsEverything { get; }

		/// <summary>Adds or removes EOF from the set. If the set doesn't change,
		/// this method may return this.</summary>
		IPGTerminalSet WithEOF(bool wantEOF = true);
		/// <summary>Creates a version of the set with IsInverted toggled.</summary>
		IPGTerminalSet Inverted();
		
		/// <summary>Generates a declaration for a variable that holds the set.</summary>
		/// <remarks>
		/// For example, if setName is foo, a set such as [aeiouy] 
		/// might use an external declaration such as 
		/// <code>IntSet foo = IntSet.Parse("[aeiouy]");</code>
		/// This method will not be called if <see cref="GenerateTest(Node)"/>
		/// never returns null.
		/// </remarks>
		Node GenerateSetDecl(Symbol setName);

		/// <summary>Generates code to test whether a terminal is in the set.</summary>
		/// <param name="subject">Represents the variable to be tested.</param>
		/// <param name="setName">Names an external set variable to use for the test.</param>
		/// <returns>A test expression such as @(la0 >= '0' && '9' >= la0), or 
		/// null if an external setName is needed and was not provided.</returns>
		/// <remarks>
		/// At first, <see cref="LLParserGenerator"/> calls this method with 
		/// <c>setName == null</c>. If it returns null, it calls the method a
		/// second time, giving the name of an external variable in which the
		/// set is held (see <see cref="GenerateSetDecl"/>).
		/// <para/>
		/// For example, if the subject is @(la0), the test for a simple set
		/// like [a-z?] might be something like <c>@((la0 >= 'a' && 'z' >= la0)
		/// || la0 == '?')</c>. When the setName is @(foo), the test might be 
		/// <c>@(foo.Contains(la0))</c> instead.
		/// </remarks>
		Node GenerateTest(Node subject, Symbol setName);

		/// <summary>Returns the number of "case" labels required for this set
		/// (ignoring ,
		/// 0 if it is an empty set, or -1 if the set cannot be used in a switch</summary>
		//int CaseCount { get; }
		//int[] Cases { get; }

		/// <summary>Simplifies the set, if possible, so that GenerateTest() can
		/// generate simpler code for an if-else chain in a prediction tree.</summary>
		/// <param name="dontcare">A set of terminals that have been ruled out,
		/// i.e. it is already known that the lookahead value is not in this set.</param>
		/// <returns>An optimized set, or this.</returns>
		IPGTerminalSet Optimize(IPGTerminalSet dontcare);

		/// <summary>Returns an example of a character in the set, or null if this 
		/// is not a set of characters or if EOF is the only member of the set.</summary>
		char? ExampleChar { get; }
		/// <summary>Returns an example of an item in the set. If the example is
		/// a character, it should be surrounded by single quotes.</summary>
		string Example { get; }
	}
	public static class PGTerminalSet
	{
		public static IPGTerminalSet Subtract(this IPGTerminalSet @this, IPGTerminalSet other) { return @this.IntersectionCore(other, true) ?? other.IntersectionCore(@this, false, true); }
		public static IPGTerminalSet Union(this IPGTerminalSet @this, IPGTerminalSet other) { return @this.UnionCore(other) ?? other.UnionCore(@this); }
		public static IPGTerminalSet Intersection(this IPGTerminalSet @this, IPGTerminalSet other) { return @this.IntersectionCore(other) ?? other.IntersectionCore(@this); }
		public static IPGTerminalSet WithoutEOF(this IPGTerminalSet @this) { return @this.WithEOF(false); }

		public static bool Overlaps(this IPGTerminalSet @this, IPGTerminalSet other)
		{
			var tmp = @this.Intersection(other);
			return !tmp.IsEmptySet;
		}
		public static bool SlowEquals(this IPGTerminalSet @this, IPGTerminalSet other)
		{
			bool e = @this.ContainsEverything;
			if (e == other.ContainsEverything && @this.ContainsEOF == other.ContainsEOF) {
				if (e)
					return true;
				var sub1 = @this.Subtract(other);
				if (sub1 == null || !sub1.IsEmptySet) return false;
				var sub2 = other.Subtract(@this);
				return sub2 != null && sub2.IsEmptySet;
			}
			return false;
		}
	}

	/// <summary>Represents a set of characters (e.g. 'A'..'Z' | 'a'..'z' | '_'), 
	/// or a set of token IDs, used in the grammar of a parser.</summary>
	/// <remarks>This class extends <see cref="IntSet"/> mainly with code 
	/// generation functionality used by <see cref="LLParserGenerator"/>.
	/// <para/>
	/// -1 is assumed to represent EOF.
	/// </remarks>
	public class PGIntSet : IntSet, IPGTerminalSet
	{
		public const int EOF_int = -1;
		public     static readonly PGIntSet EOF = PGIntSet.With(-1);
		public new static readonly PGIntSet All = new PGIntSet(false, true);
		public     static readonly PGIntSet AllExceptEOF = PGIntSet.Without(-1);
		public new static readonly PGIntSet Empty = new PGIntSet();

		public bool IsSymbolSet { get; set; }
		
		public bool ContainsEOF { get { return Contains(EOF_int); } }
		IPGTerminalSet IPGTerminalSet.WithEOF(bool wantEOF) { return WithEOF(wantEOF); }
		public PGIntSet WithEOF(bool wantEOF = true)
		{
			if (wantEOF == ContainsEOF)
				return this;
			return wantEOF ? Union(EOF) : Subtract(EOF);
		}
		IPGTerminalSet IPGTerminalSet.Inverted() { return (PGIntSet)Inverted(); }

		public new static PGIntSet With(params int[] members) { return new PGIntSet(false, false, false, members); }
		public new static PGIntSet WithRanges(params int[] ranges) { return new PGIntSet(false, false, true, ranges); }
		public new static PGIntSet Without(params int[] members) { return new PGIntSet(false, true, false, members); }
		public new static PGIntSet WithoutRanges(params int[] ranges) { return new PGIntSet(false, true, true, ranges); }
		public new static PGIntSet WithChars(params int[] members) { return new PGIntSet(true, false, false, members); }
		public new static PGIntSet WithCharRanges(params int[] ranges) { return new PGIntSet(true, false, true, ranges); }
		public new static PGIntSet WithoutChars(params int[] members) { return new PGIntSet(true, true, false, members); }
		public new static PGIntSet WithoutCharRanges(params int[] ranges) { return new PGIntSet(true, true, true, ranges); }
		public     static PGIntSet With(params Symbol[] members) { return new PGIntSet(false, members); }
		public     static PGIntSet Without(params Symbol[] members) { return new PGIntSet(true, members); }

		public new static PGIntSet Parse(string members)
		{
			int errorIndex;
			var set = TryParse(members, out errorIndex);
			if (set == null)
				throw new FormatException(string.Format(
					"Input string could not be parsed to a PGIntSet (error at index {0})", errorIndex));
			return set;
		}
		public new static PGIntSet TryParse(string members)
		{
			int _;
			return TryParse(members, out _);
		}
		public new static PGIntSet TryParse(string members, out int errorIndex)
		{
			bool isCharSet, inverted;
			InternalList<IntRange> ranges;
			if (!TryParse(members, out isCharSet, out ranges, out inverted, out errorIndex))
				return null;
			return new PGIntSet(isCharSet, ranges, inverted, true);
		}

		public PGIntSet(bool isCharSet = false, bool inverted = false) : base(isCharSet, inverted) { }
		public PGIntSet(IntRange r, bool isCharSet = false, bool inverted = false) : base(r, isCharSet, inverted) {}
		public PGIntSet(bool isCharSet, bool inverted, params IntRange[] list) : base(isCharSet, inverted, list) {}
		public PGIntSet(bool inverted, params Symbol[] list) : base(false, inverted, false, list.Select(s => s.Id).ToArray()) { }
		protected PGIntSet(bool isCharSet, InternalList<IntRange> ranges, bool inverted, bool autoSimplify) : base(isCharSet, ranges, inverted, autoSimplify) { }
		protected PGIntSet(bool isCharSet, bool inverted, bool ranges, params int[] list) : base(isCharSet, inverted, ranges, list) { }

		protected override IntSet New(IntSet basis, bool inverted, InternalList<IntRange> ranges)
		{
			return new PGIntSet(basis.IsCharSet, ranges, inverted, false) { 
				IsSymbolSet = ((PGIntSet)basis).IsSymbolSet
			};
		}

		IPGTerminalSet IPGTerminalSet.UnionCore(IPGTerminalSet other)
		{
			var other_ = other as IntSet;
			if (other_ == null) return null;
			return Union(other_);
		}
		public PGIntSet Union(IntSet other)
		{
			return (PGIntSet)base.Union(other, true);
		}
		IPGTerminalSet IPGTerminalSet.IntersectionCore(IPGTerminalSet other, bool subtract, bool subtractThis)
		{
			var other_ = other as IntSet;
			if (other_ == null) return null;
			return Intersection(other_, subtract, subtractThis);
		}
		new public PGIntSet Intersection(IntSet other, bool subtract = false, bool subtractThis = false)
		{
			return (PGIntSet)base.Intersection(other, subtract, subtractThis);
		}
		new public PGIntSet Subtract(IntSet other)
		{
			return Intersection(other, true);
		}

		static GreenFactory F = new GreenFactory(new EmptySourceFile("PGSets.cs"));
		static readonly Symbol _setName = GSymbol.Get("setName");
		static readonly Symbol _IntSet = GSymbol.Get("IntSet");
		static readonly Symbol _Parse = GSymbol.Get("Parse");
		static readonly Symbol _Id = GSymbol.Get("Id");
		static readonly Symbol _Contains = GSymbol.Get("Contains");
		static readonly Symbol _With = GSymbol.Get("With");
		static readonly Symbol _Without = GSymbol.Get("Without");
		static readonly GreenNode _false = F.Literal(false);
		// static readonly IntSet setName = IntSet.With(...);
		static readonly GreenNode _symbolSetWith = F.Attr(F.Symbol(S.Static), F.Symbol(S.Readonly),
			F.Var(F.Symbol("IntSet"), F.Call(_setName, 
				F.Call(F.Dot(_IntSet, _With)))));
		// static readonly IntSet setName = IntSet.Without(...);
		static readonly GreenNode _symbolSetWithout = F.Attr(F.Symbol(S.Static), F.Symbol(S.Readonly),
			F.Var(F.Symbol("IntSet"), F.Call(_setName, 
				F.Call(F.Dot(_IntSet, _Without)))));
		// static readonly IntSet setName = IntSet.Parse(...)
		static readonly GreenNode _setDecl = F.Attr(F.Symbol(S.Static), F.Symbol(S.Readonly),
			F.Var(F.Symbol("IntSet"), F.Call(_setName,
				F.Call(F.Dot(_IntSet, _Parse)))));

		public Node GenerateSetDecl(Symbol setName)
		{
			GreenNode basis = IsSymbolSet ? (IsInverted ? _symbolSetWithout : _symbolSetWith) : _setDecl;
			basis.Freeze();
			Node setDecl = Node.FromGreen(basis, -1);
			Node var = setDecl.Args[1];
			var.Name = setName;
			Node initializer = var.Args[0];

			if (IsSymbolSet) {
				var args = initializer.Args;
				for (int i = 0; i < _ranges.Count; i++)
					for (int n = _ranges[i].Lo; n <= _ranges[i].Hi; n++)
						args.Add(Node.FromGreen(F.Dot(F.Literal(GSymbol.GetById(n)), F.Symbol(_Id))));
			} else {
				var args = initializer.Args;
				args.Add(Node.FromGreen(F.Literal(this.ToString())));
			}
			return setDecl;
		}

		/// <summary>Returns the "complexity" of the set.</summary>
		/// <remarks>The parser generator tests simple sets such as "la0 == ' ' || 
		/// la0 == '\t'" inline using an expression, but large sets are stored in 
		/// variables and tested by calling a method. Complexity() is used to 
		/// decide which approach is more appropriate.</remarks>
		public int Complexity(int singleCountsAs, int rangeCountsAs, bool countEOF)
		{
			if (IsSymbolSet)
				return (int)SizeIgnoringInversion - (countEOF && ContainsEOF ^ IsInverted ? 1 : 0);
			else {
				int result = 0;
				for (int i = 0; i < _ranges.Count; i++)
				{
					var r = _ranges[i];
					int dif = r.Hi - r.Lo;
					if (!countEOF && r.Contains(EOF_int) && --dif < 0)
						continue;
					result += (dif == 0 ? singleCountsAs : rangeCountsAs);
				}
				return result;
			}
		}

		public Node GenerateTest(Node subject, Symbol setName)
		{
			if (setName != null)
			{
				// setName.Contains(...)
				Node result = Node.FromGreen(F.Call(F.Dot(setName, _Contains)));
				result.Args.Add(subject);
				return result;
			}
			else
			{
				if (_ranges.Count >= 3 && Complexity(1, 2, true) > 5)
					return null; // complex

				GreenNode test, result = null;
				for (int i = 0; i < _ranges.Count; i++)
				{
					var r = _ranges[i];
					if (IsSymbolSet) {
						for (int id = r.Lo; id <= r.Hi; id++) {
							test = F.Call(S.Eq, subject.FrozenGreen, F.Literal(GSymbol.GetById(id)));
							AddTest(ref result, test);
						}
					} else {
						if (r.Lo == r.Hi)
							test = F.Call(S.Eq, subject.FrozenGreen, MakeLiteral(r.Lo));
						else
							test = F.Call(S.And, F.Call(S.GE, subject.FrozenGreen, MakeLiteral(r.Lo)),
							                     F.Call(S.LE, subject.FrozenGreen, MakeLiteral(r.Hi)));
						AddTest(ref result, test);
					}
				}
				if (IsInverted)
				{
					if (result == null)
						return Node.FromGreen(F.@true);
					if (result.Name == S.Eq) {
						result = result.Unfrozen();
						result.Name_set(S.Neq);
					} else {
						result = F.Call(S.Not, F.InParens(result));
					}
				}
				result = result ?? F.@false;
				return Node.FromGreen(result);
			}
		}
		internal GreenNode MakeLiteral(int c)
		{
			if (IsSymbolSet)
				return F.Literal(GSymbol.GetById(c));
			else if (IsCharSet && c >= 0 && new IntRange(c).CanPrintAsCharRange)
				return F.Literal((char)c);
			else
				return F.Literal(c);
		}
		private void AddTest(ref GreenNode result, GreenNode test)
		{
			if (result == null)
				result = test;
			else
				result = F.Call(S.Or, result, test);
		}

		IPGTerminalSet IPGTerminalSet.Optimize(IPGTerminalSet dontcare) { return Optimize(dontcare as IntSet); }
		public PGIntSet Optimize(IntSet dontcare)
		{
			/*PGIntSet optimized;
			if (IsSymbolSet) {
				// In a symbol set, runs are not allowed, so optimize pointwise.
				if (Inverted) {
					if (dontcare.Inverted)
						optimized = Intersection(dontcare, false, true); // dontcare.Subtract(this)
					else
						optimized = Union(dontcare);
					Debug.Assert(!optimized.Inverted);
					optimized.Inverted = true;
				} else {
					optimized = Subtract(dontcare);
				}
				Debug.Assert(optimized.SizeIgnoringInversion <= SizeIgnoringInversion);
				if (optimized.SizeIgnoringInversion < SizeIgnoringInversion)
					return optimized;
			}*/
			return (PGIntSet)base.Optimize(dontcare, !IsSymbolSet);
		}

		public int? ExampleInt
		{
			get {
				if (IsCharSet && IsInverted && Contains('_'))
					return '_';
				if (IsEmptySet)
					return null;
				int example = int.MinValue;
				int min = IsCharSet ? 32 : 0;
				foreach (var range in Runs()) {
					example = range.Lo < min ? range.Hi : range.Lo;
					if (example > min)
						break;
				}
				return example;
			}
		}
		public char? ExampleChar
		{
			get {
				if (!IsCharSet)
					return null;
				int? ex = ExampleInt;
				char c;
				if (ex == null || (c = (char)ex.Value) != ex.Value)
					return null;
				return c;
			}
		}
		public string Example
		{
			get {
				char? ch = ExampleChar;
				if (ch != null)
					return ch == '\'' ? @"'\''" : string.Format("'{0}'", ch);
				int? ex = ExampleInt;
				if (ex == null)
					return "<nothing>";
				if (ex == EOF_int)
					return "<EOF>";
				Symbol s;
				if (IsSymbolSet && (s = GSymbol.GetById(ex.Value)) != null)
					return "$" + s.ToString();
				return ex.Value.ToString();
			}
		}

		public bool Equals(IPGTerminalSet other)
		{
			if (other is IntSet)
				return Equals((IntSet)other);
			else
				return this.SlowEquals(other);
		}
	}


	/// <summary>Represents a terminal set that matches all terminals or none of 
	/// them, and may or may not match EOF. The <see cref="LLParserGenerator"/>
	/// uses this class so that it can define empty and "all" sets without caring 
	/// what concrete type of <see cref="IPGTerminalSet"/> is used to match 
	/// terminals in the parser being generated.</summary>
	/// <remarks>This object is considered to be empty when Inverted=false, and
	/// to match anything when Inverted=true. The value of <see cref="ContainsEOF"/>
	/// inverts when you change <see cref="Inverted"/>.</remarks>
	public class TrivialTerminalSet : IPGTerminalSet
	{
		public static readonly TrivialTerminalSet Empty        = new TrivialTerminalSet(false, false);
		public static readonly TrivialTerminalSet All          = new TrivialTerminalSet(true, true);
		public static readonly TrivialTerminalSet EOF          = new TrivialTerminalSet(true, false);
		public static readonly TrivialTerminalSet AllExceptEOF = new TrivialTerminalSet(false, true);

		public TrivialTerminalSet(bool hasEOF = false, bool hasEverythingElse = false) 
		{
			_hasEOF = hasEOF ^ hasEverythingElse;
			_inverted = hasEverythingElse;
		}

		public IPGTerminalSet WithEOF(bool wantEOF)
		{
			if (_inverted)
				return wantEOF ? All : AllExceptEOF;
			else
				return wantEOF ? EOF : Empty;
		}
		public IPGTerminalSet Inverted()
		{
			if (_inverted)
				return ContainsEOF ? Empty : EOF;
			else
				return ContainsEOF ? AllExceptEOF : All;
		}

		public IPGTerminalSet UnionCore(IPGTerminalSet other)
		{
			if (_inverted)
			{
				return (ContainsEOF || other.ContainsEOF) ? All : AllExceptEOF;
			}
			else
			{
				if (ContainsEOF && !other.ContainsEOF)
					other = other.WithEOF();
				return other;
			}
		}
		public IPGTerminalSet IntersectionCore(IPGTerminalSet other, bool subtract, bool subtractThis)
		{
			var outputEOF = other.ContainsEOF ^ subtract && ContainsEOF ^ subtractThis;
			
			
			/*
			The underlying cases:
			if (subtract) {
				if (_inverted) {
					other = other.Clone();
					other.Inverted = !other.Inverted;
					other.ContainsEOF = outputEOF;
				} else {
					return new TrivialTerminalSet() {
						Inverted = false,
						ContainsEOF = outputEOF
					};
				}
			}
			if (subtractThis) {
				if (_inverted) {
					return new TrivialTerminalSet() {
						Inverted = false,
						ContainsEOF = outputEOF
					};
				} else {
					other = other.Clone();
					other.ContainsEOF = outputEOF;
					return other;
				}
			}
			if (_inverted) {
				other = other.Clone();
				other.ContainsEOF = outputEOF;
				return other;
			} else {
				return new TrivialTerminalSet() {
					Inverted = false,
					ContainsEOF = outputEOF
				};
			}*/
			

			
			if (_inverted ^ subtractThis) {
				if (subtract) {
					Debug.Assert(_inverted);
					other = other.Inverted();
				}
				other = other.WithEOF(outputEOF);
				return other;
			} else {
				return outputEOF ? EOF : Empty;
			}
		}

		bool _hasEOF, _inverted;
		public bool ContainsEOF { get { return _hasEOF ^ _inverted; } }
		public bool IsInverted { get { return _inverted; } }
		public bool IsEmptySet { get { return !_inverted && !_hasEOF; } }
		public bool ContainsEverything { get { return _inverted && !_hasEOF; } }

		static GreenFactory F = new GreenFactory(new EmptySourceFile("PGSets.cs"));
		public Node GenerateSetDecl(Symbol setName)
		{
			return Node.NewSynthetic(S.Missing, F.File);
		}
		public Node GenerateTest(Node subject, Symbol setName)
		{
			// !ContainsEOF && !Inverted: @(false)
			// !ContainsEOF && Inverted: @(subject != -1)
			// ContainsEOF && !Inverted: @(subject == -1)
			// ContainsEOF && Inverted: @(true)
			if (_hasEOF)
				return Node.FromGreen(F.Call(IsInverted ? S.Neq : S.Eq, subject.FrozenGreen, F.Literal(PGIntSet.EOF_int)));
			else
				return Node.FromGreen(F.Literal(IsInverted));
		}

		public override string ToString()
		{
			if (_inverted)
				return _hasEOF ? @" [^\$] " : " [^] ";
			else
				return _hasEOF ? @" [\$] " : " [] ";
		}

		IPGTerminalSet IPGTerminalSet.Optimize(IPGTerminalSet dontcare) { return this; }

		public char? ExampleChar { get { return null; } }
		public string Example { get { return IsInverted ? "<anything>" : ContainsEOF ? "<EOF>" : "<nothing>"; } }

		public bool Equals(IPGTerminalSet other)
		{
			var t = other as TrivialTerminalSet;
			if (t != null)
				return t._hasEOF == _hasEOF && t._inverted == _inverted;
			else
				return this.SlowEquals(other);
		}

		public PGIntSet ToIntSet(bool charSet)
		{
			if (_hasEOF)
				return new PGIntSet(new IntRange(PGIntSet.EOF_int), charSet, _inverted);
			else
				return new PGIntSet(charSet, _inverted);
		}
	}
}
