﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Loyc;
using Loyc.Syntax;
using Loyc.Collections;
using S = Loyc.Syntax.CodeSymbols;
using Loyc.Math;

namespace LeMP
{
	public partial class StandardMacros
	{
		static readonly Symbol TupleMakers = (Symbol)"StandardMacros.TupleMakers";
		static readonly Symbol DefaultTupleMaker = (Symbol)"StandardMacros.DefaultTupleMaker";
		static readonly LNode id_Tuple = LNode.Id("Tuple"), Tuple_Create = LNode.Call(S.Dot, new RVList<LNode>(id_Tuple, LNode.Id("Create")));

		static IList<Pair<LNode, LNode>> MaybeInitTupleMakers(IDictionary<object, object> properties)
		{
			IList<Pair<LNode, LNode>> result;
			if ((result = properties.TryGetValue(TupleMakers, null) as IList<Pair<LNode, LNode>>) == null)
			{
				var pair = Pair.Create(id_Tuple, Tuple_Create);
				properties[DefaultTupleMaker] = pair;
				properties[TupleMakers] = result = new List<Pair<LNode, LNode>>() {
					Pair.Create<LNode,LNode>(null, null),  // ignore 0 args
					pair,                                  // 1 arg
				};
			}
			return result;
		}

		[LexicalMacro("use_default_tuple_types()", "")]
		public static LNode use_default_tuple_types(LNode node, IMacroContext context)
		{
			if (node.ArgCount != 0)
				return null;
			context.ScopedProperties.Remove(TupleMakers);
			context.ScopedProperties.Remove(DefaultTupleMaker);
			return F.Call(S.Splice);
		}

		[LexicalMacro("set_tuple_type(BareName); set_tuple_type(TupleSize, BareName); set_tuple_type(TupleSize, BareName, Factory.Method)",
			"Set type and creation method for tuples, for a specific size of tuple or for all sizes at once")]
		public static LNode set_tuple_type(LNode node, IMacroContext context) {
			var tupleMakers = MaybeInitTupleMakers(context.ScopedProperties);

			int? size = node.Args[0, F._Missing].Value as int?;
			var rest = node.Slice(size != null ? 1 : 0);
			if (!MathEx.IsInRange(rest.Count, 1, 2)) 
				return Reject(context, node, "Incorrect number of arguments");
			var tupleCfg = Pair.Create(rest[0], rest.TryGet(1, null));
			if (tupleCfg.A.Value == null)
				tupleCfg.A = null; // Makes us ignore tuples of this size
			if (tupleCfg.B == null && tupleCfg.A != null)
				tupleCfg.B = F.Dot(tupleCfg.A, F.Id("Create"));

			if (size == null) {
				tupleMakers.Resize(1);
				context.ScopedProperties[DefaultTupleMaker] = tupleCfg;
			} else {
				while (tupleMakers.Count <= size.Value)
					tupleMakers.Add((Pair<LNode,LNode>)context.ScopedProperties[DefaultTupleMaker]);
				tupleMakers[size.Value] = tupleCfg;
			}
			return F.Call(S.Splice);
		}

		[LexicalMacro("#<x, y, ...>", "Represents a tuple type", "#of")]
		public static LNode TupleType(LNode node, IMacroContext context)
		{
			var stem = node.Args[0, F._Missing];
			if (stem.IsId && (stem.Name == S.List || stem.Name == S.Tuple)) {
				var tupleMakers = MaybeInitTupleMakers(context.ScopedProperties);
				
				var bareType = tupleMakers.TryGet(node.Args.Count - 1, new Pair<LNode, LNode>()).A;
				if (bareType == null)
					bareType = ((Pair<LNode, LNode>)context.ScopedProperties[DefaultTupleMaker]).A;
				if (bareType != null)
					return node.WithArgChanged(0, bareType);
			}
			return null;
		}

		[LexicalMacro("(x,); (x, y, ...)", "Create a tuple", "#tuple")]
		public static LNode Tuple(LNode node, IMacroContext context)
		{
			// TODO: consider supporting .[a, b] (and !(a, b)) as syntax for @``<a, b> 
			// which will refer to a tuple type.
			if (node.IsCall) {
				// Do not change a tuple on the LHS of =>, i.e. `(x, y) => expr`
				if (context.Parent == null || (context.Parent.Calls(S.Lambda, 2) && context.Parent.Args[0].Equals(node)))
					return null;
				var props = context.ScopedProperties;
				var tupleMakers = MaybeInitTupleMakers(props);
				LNode method = (node.ArgCount < tupleMakers.Count 
					? tupleMakers[node.ArgCount] : ((Pair<LNode, LNode>)props[DefaultTupleMaker])).B;
				if (method != null)
					return node.WithTarget(method);
			}
			return null;
		}

		// In EC# we should support cases like "if (Foo[(a, b) = expr]) {...}"
		// This macro targets plain C# where that is not possible.
		[LexicalMacro("(a, b, etc) = expr;", "Assign a = expr.Item1, b = expr.Item2, etc.", "=")]
		public static LNode UnpackTuple(LNode node, IMessageSink sink)
		{
			var a = node.Args;
			if (a.Count == 2 && a[0].CallsMin(S.Tuple, 1)) {
				var stmts = new RWList<LNode>();
				var tuple = a[0].Args;
				var rhs = a[1];
				bool needTemp = rhs.IsCall || !char.IsLower(rhs.Name.Name.TryGet(0, '\0'));
				if (needTemp) {
					LNode tmp = F.Id(NextTempName());
					stmts.Add(F.Var(F._Missing, tmp.Name, rhs));
					rhs = tmp;
				}
				for (int i = 0; i < tuple.Count; i++)
					stmts.Add(F.Call(S.Assign, tuple[i], F.Dot(rhs, F.Id(GSymbol.Get("Item" + (i + 1))))));
				return F.Call(S.Splice, stmts.ToRVList());
			}
			return null;
		}
	}
}
