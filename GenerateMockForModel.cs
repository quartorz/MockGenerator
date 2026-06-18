using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

#nullable enable

namespace MockGenereator
{
	internal enum MemberKind
	{
		Property,
		Method,
		Event,
		/// <summary>Pre-rendered text deduped by <see cref="MemberFragment.Key"/> verbatim (generic-interface accessor + routing).</summary>
		Raw,
	}

	/// <summary>
	/// A single mock member rendered with no indentation (i == ""). The consuming generator
	/// re-indents it when splicing into the partial class. <see cref="Key"/> identifies the member
	/// for de-duplication across multiple interfaces.
	/// </summary>
	internal sealed record class MemberFragment(MemberKind Kind, string Key, string Text);

	/// <summary>
	/// An interface argument of <c>[GenerateMockFor]</c> that resolved to a real symbol at the time
	/// the attribute was processed. Its members are rendered immediately.
	/// </summary>
	internal sealed class ResolvedInterface : IEquatable<ResolvedInterface>
	{
		public string QualifiedName { get; }
		public MemberFragment[] Members { get; }

		public ResolvedInterface(string qualifiedName, MemberFragment[] members)
		{
			QualifiedName = qualifiedName;
			Members = members;
		}

		public bool Equals(ResolvedInterface? other)
		{
			if (other is null) return false;
			if (ReferenceEquals(this, other)) return true;
			return QualifiedName == other.QualifiedName && Members.AsSpan().SequenceEqual(other.Members);
		}

		public override bool Equals(object? obj) => obj is ResolvedInterface r && Equals(r);

		public override int GetHashCode()
		{
			var hash = QualifiedName.GetHashCode();
			foreach (var m in Members) hash ^= m.GetHashCode();
			return hash;
		}
	}

	/// <summary>
	/// An interface argument of <c>[GenerateMockFor]</c> that did not resolve to a real symbol —
	/// typically because it is produced by <c>[GenerateInterface]</c> in the same compilation, which
	/// is invisible to this generator. It is matched against the collected interface models later.
	/// </summary>
	internal sealed class UnresolvedInterface : IEquatable<UnresolvedInterface>
	{
		public string SimpleName { get; }
		public string? NamespaceHint { get; }
		public LocationInfo? Location { get; }

		public UnresolvedInterface(string simpleName, string? namespaceHint, LocationInfo? location)
		{
			SimpleName = simpleName;
			NamespaceHint = namespaceHint;
			Location = location;
		}

		public bool Equals(UnresolvedInterface? other)
		{
			if (other is null) return false;
			if (ReferenceEquals(this, other)) return true;
			return SimpleName == other.SimpleName && NamespaceHint == other.NamespaceHint &&
				Equals(Location, other.Location);
		}

		public override bool Equals(object? obj) => obj is UnresolvedInterface u && Equals(u);

		public override int GetHashCode()
		{
			var hash = SimpleName.GetHashCode();
			hash ^= NamespaceHint?.GetHashCode() ?? 0;
			hash ^= Location?.GetHashCode() ?? 0;
			return hash;
		}
	}

	/// <summary>
	/// A <c>[GenerateMockFor]</c> target class and the interfaces it was asked to mock.
	/// </summary>
	internal sealed class MockTarget : IEquatable<MockTarget>
	{
		public string? Namespace { get; }
		public string ClassName { get; }
		public string Generics { get; }
		public string Constraints { get; }
		public ResolvedInterface[] Resolved { get; }
		public UnresolvedInterface[] Unresolved { get; }

		public MockTarget(string? @namespace, string className, string generics, string constraints,
			ResolvedInterface[] resolved, UnresolvedInterface[] unresolved)
		{
			Namespace = @namespace;
			ClassName = className;
			Generics = generics;
			Constraints = constraints;
			Resolved = resolved;
			Unresolved = unresolved;
		}

		public bool Equals(MockTarget? other)
		{
			if (other is null) return false;
			if (ReferenceEquals(this, other)) return true;
			return Namespace == other.Namespace && ClassName == other.ClassName &&
				Generics == other.Generics && Constraints == other.Constraints &&
				Resolved.AsSpan().SequenceEqual(other.Resolved) &&
				Unresolved.AsSpan().SequenceEqual(other.Unresolved);
		}

		public override bool Equals(object? obj) => obj is MockTarget t && Equals(t);

		public override int GetHashCode()
		{
			var hash = (Namespace?.GetHashCode() ?? 0) ^ ClassName.GetHashCode();
			foreach (var r in Resolved) hash ^= r.GetHashCode();
			foreach (var u in Unresolved) hash ^= u.GetHashCode();
			return hash;
		}
	}

	/// <summary>
	/// The interface that a <c>[GenerateInterface]</c> class produces, projected to the data needed
	/// to mock it. Members are rendered as <see cref="MemberFragment"/> so this model is fully
	/// value-equatable and holds no symbols.
	/// </summary>
	internal sealed class InterfaceMockModel : IEquatable<InterfaceMockModel>
	{
		public string? Namespace { get; }
		public string InterfaceName { get; }
		public string Generics { get; }
		public MemberFragment[] Members { get; }

		public InterfaceMockModel(string? @namespace, string interfaceName, string generics, MemberFragment[] members)
		{
			Namespace = @namespace;
			InterfaceName = interfaceName;
			Generics = generics;
			Members = members;
		}

		public string QualifiedName =>
			(Namespace == null ? "global::" : $"global::{Namespace}.") + InterfaceName + Generics;

		public bool Equals(InterfaceMockModel? other)
		{
			if (other is null) return false;
			if (ReferenceEquals(this, other)) return true;
			return Namespace == other.Namespace && InterfaceName == other.InterfaceName &&
				Generics == other.Generics && Members.AsSpan().SequenceEqual(other.Members);
		}

		public override bool Equals(object? obj) => obj is InterfaceMockModel m && Equals(m);

		public override int GetHashCode()
		{
			var hash = (Namespace?.GetHashCode() ?? 0) ^ InterfaceName.GetHashCode() ^ Generics.GetHashCode();
			foreach (var m in Members) hash ^= m.GetHashCode();
			return hash;
		}
	}

	internal static class MockMembers
	{
		/// <summary>
		/// Renders a single non-method mock member with no indentation. Returns null for members that are
		/// not mockable (indexers, etc.) or for ordinary methods — methods are grouped by name so overloads
		/// share one fragment (see <see cref="AddMember"/>).
		/// </summary>
		public static MemberFragment? Render(ISymbol member)
		{
			switch (member)
			{
				case IPropertySymbol p when !p.IsIndexer:
				{
					using var _ = StringBuilderHolder.Get(out var sb);
					sb.EmitMockPropertyMember("", p);
					return new MemberFragment(MemberKind.Property, p.Name, sb.ToString());
				}
				case IEventSymbol e:
				{
					using var _ = StringBuilderHolder.Get(out var sb);
					sb.EmitMockEventMember("", e);
					return new MemberFragment(MemberKind.Event, e.Name, sb.ToString());
				}
			}
			return null;
		}

		/// <summary>
		/// Routes one member: ordinary methods are collected into <paramref name="methods"/> (so overloads
		/// of the same name land in one group); everything else is rendered immediately into <paramref name="result"/>.
		/// </summary>
		static void AddMember(ISymbol member, List<MemberFragment> result, Utilities.MethodGroups methods)
		{
			if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
			{
				methods.Add(m.Name, m);
				return;
			}
			var fragment = Render(member);
			if (fragment != null) result.Add(fragment);
		}

		/// <summary>
		/// Renders one fragment per collected method-name group, so each overload set forms a single member.
		/// </summary>
		static void AppendMethodFragments(List<MemberFragment> result, Utilities.MethodGroups methods)
		{
			methods.ForEachGroup((name, overloads) =>
			{
				using var _ = StringBuilderHolder.Get(out var gsb);
				gsb.EmitMockMethodGroup("", name, overloads);
				result.Add(new MemberFragment(MemberKind.Method, name, gsb.ToString()));
			});
		}

		/// <summary>
		/// Renders the mock members for a real interface symbol, including its base interfaces.
		/// Non-generic interfaces emit flat public members; generic interfaces use the
		/// <c>As{Iface}&lt;...&gt;()</c> accessor scheme so distinct closed type arguments never collide.
		/// </summary>
		public static MemberFragment[] RenderInterface(INamedTypeSymbol iface)
		{
			var result = new List<MemberFragment>();

			// Gather the interface plus all its transitive bases, deduped.
			var allIfaces = new List<INamedTypeSymbol>();
			var seenIface = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			if (seenIface.Add(iface)) allIfaces.Add(iface);
			foreach (var baseIface in iface.AllInterfaces)
			{
				if (seenIface.Add(baseIface)) allIfaces.Add(baseIface);
			}

			// Non-generic interfaces contribute flat members; generic interfaces use the accessor scheme.
			var genericDefs = new List<INamedTypeSymbol>();
			var genericClosed = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
			var props = new List<IPropertySymbol>();
			var events = new List<IEventSymbol>();
			var ordinaryMethods = new List<IMethodSymbol>();
			foreach (var current in allIfaces)
			{
				if (current.OriginalDefinition.TypeParameters.Length > 0)
				{
					var def = current.OriginalDefinition;
					if (!genericClosed.TryGetValue(def, out var list))
					{
						list = new List<INamedTypeSymbol>();
						genericClosed[def] = list;
						genericDefs.Add(def);
					}
					if (!list.Any(x => SymbolEqualityComparer.Default.Equals(x, current)))
					{
						list.Add(current);
					}
				}
				else
				{
					foreach (var member in current.GetMembers())
					{
						switch (member)
						{
							case IPropertySymbol p when !p.IsIndexer: props.Add(p); break;
							case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary: ordinaryMethods.Add(m); break;
							case IEventSymbol e: events.Add(e); break;
						}
					}
				}
			}

			EmitProperties(result, props);
			EmitEvents(result, events);
			EmitMethods(result, ordinaryMethods);

			foreach (var def in genericDefs)
			{
				AppendGenericAccessorFragments(result, def, genericClosed[def]);
			}

			return result.ToArray();
		}

		/// <summary>
		/// Emits properties gathered from a root interface and its bases. Same-named properties of the same
		/// type collapse to one implicit public member; same-named properties of <em>different</em> types
		/// (a conflicting diamond) become per-interface explicit interface implementations, since one public
		/// member cannot satisfy both.
		/// </summary>
		static void EmitProperties(List<MemberFragment> result, List<IPropertySymbol> props)
		{
			foreach (var byName in props.GroupBy(p => p.Name))
			{
				var list = byName.ToList();
				var conflict = list.Select(p => p.Type.QualifiedName()).Distinct().Count() > 1;
				if (!conflict)
				{
					using var _ = StringBuilderHolder.Get(out var sb);
					sb.EmitMockPropertyMember("", list[0]);
					result.Add(new MemberFragment(MemberKind.Property, list[0].Name, sb.ToString()));
				}
				else
				{
					var seenTarget = new HashSet<string>();
					foreach (var p in list)
					{
						var ifaceQ = p.ContainingType.QualifiedName();
						if (!seenTarget.Add(ifaceQ)) continue;
						var prefix = p.ContainingType.Name + "_";
						using var _ = StringBuilderHolder.Get(out var sb);
						sb.EmitMockPropertySlot("", p, prefix + p.Name, prefix, ifaceQ);
						result.Add(new MemberFragment(MemberKind.Raw, "GPX:" + ifaceQ + "." + p.Name, sb.ToString()));
					}
				}
			}
		}

		/// <summary>
		/// Emits events gathered from a root interface and its bases. Mirrors <see cref="EmitProperties"/>:
		/// same-named events of the same delegate type collapse to one implicit public event; differing
		/// types become per-interface explicit interface implementations.
		/// </summary>
		static void EmitEvents(List<MemberFragment> result, List<IEventSymbol> events)
		{
			foreach (var byName in events.GroupBy(e => e.Name))
			{
				var list = byName.ToList();
				var conflict = list.Select(e => e.Type.QualifiedName()).Distinct().Count() > 1;
				if (!conflict)
				{
					using var _ = StringBuilderHolder.Get(out var sb);
					sb.EmitMockEventMember("", list[0]);
					result.Add(new MemberFragment(MemberKind.Event, list[0].Name, sb.ToString()));
				}
				else
				{
					var seenTarget = new HashSet<string>();
					foreach (var e in list)
					{
						var ifaceQ = e.ContainingType.QualifiedName();
						if (!seenTarget.Add(ifaceQ)) continue;
						var prefix = e.ContainingType.Name + "_";
						using var _ = StringBuilderHolder.Get(out var sb);
						sb.EmitMockEventSlot("", e, prefix + e.Name, ifaceQ);
						result.Add(new MemberFragment(MemberKind.Raw, "GEX:" + ifaceQ + "." + e.Name, sb.ToString()));
					}
				}
			}
		}

		/// <summary>
		/// Emits methods gathered from a root interface and its bases. Overloads (same name, different
		/// parameters) merge into one public group as usual. The one case a public member cannot express —
		/// same name and parameters but a <em>different return type</em> across interfaces — is emitted as
		/// per-interface explicit interface implementations instead.
		/// </summary>
		static void EmitMethods(List<MemberFragment> result, List<IMethodSymbol> ordinaryMethods)
		{
			static string Sig(IMethodSymbol m) => m.Name + "`" + m.Arity + m.MethodParamTypes();

			var conflictSigs = new HashSet<string>();
			foreach (var bySig in ordinaryMethods.GroupBy(Sig))
			{
				if (bySig.Select(m => m.ReturnTypeName()).Distinct().Count() > 1) conflictSigs.Add(bySig.Key);
			}

			var methods = new Utilities.MethodGroups();
			var seenExplicit = new HashSet<string>();
			foreach (var m in ordinaryMethods)
			{
				if (!conflictSigs.Contains(Sig(m)))
				{
					methods.Add(m.Name, m);
					continue;
				}
				var ifaceQ = m.ContainingType.QualifiedName();
				var key = "GMX:" + ifaceQ + "." + m.Name + m.TypeParameters.GenericsParams() + m.MethodParamTypes();
				if (!seenExplicit.Add(key)) continue;
				using var _ = StringBuilderHolder.Get(out var sb);
				sb.EmitMockMethodGroup("", m.ContainingType.Name + "_" + m.Name, new[] { m }, ifaceQ);
				result.Add(new MemberFragment(MemberKind.Raw, key, sb.ToString()));
			}

			AppendMethodFragments(result, methods);
		}

		/// <summary>
		/// Emits the accessor scheme for one generic interface definition as <see cref="MemberKind.Raw"/>
		/// fragments: one accessor-class+<c>As{Iface}&lt;...&gt;()</c> fragment (deduped by definition) and one
		/// explicit-interface-implementation fragment per closed-instantiation member (deduped by target+member),
		/// so the accessor is shared while distinct closed instantiations each get their own routing.
		/// </summary>
		static void AppendGenericAccessorFragments(List<MemberFragment> result, INamedTypeSymbol def, List<INamedTypeSymbol> closedList)
		{
			var accName = def.Name + "Accessor";
			var asName = "As" + def.Name;
			var tparams = def.TypeParameters.GenericsParams();
			var tconstraints = def.TypeParameters.GenericsConstraints();
			var mapField = "_" + char.ToLower(def.Name[0]) + def.Name.Substring(1) + "AccessorMap";

			// Accessor type + As<...>() entry (rendered at i == ""; the consuming generator re-indents).
			{
				const string i = "";
				const string inner = "\t";
				using var _ = StringBuilderHolder.Get(out var sb);
				sb.Append($"\n{i}public sealed class {accName}{tparams}{tconstraints}");
				sb.Append($"\n{i}{{");
				var accMethods = new Utilities.MethodGroups();
				foreach (var member in def.GetMembers())
				{
					switch (member)
					{
						case IPropertySymbol p when !p.IsIndexer:
							sb.EmitMockPropertyMember(inner, p);
							break;
						case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
							accMethods.Add(m.Name, m);
							break;
						case IEventSymbol e:
							sb.EmitMockEventMember(inner, e);
							break;
					}
				}
				accMethods.EmitAll(sb, inner);
				sb.Append($"\n{i}}}");

				sb.Append($"\n{i}private readonly System.Collections.Generic.Dictionary<System.Type, object> {mapField} = new System.Collections.Generic.Dictionary<System.Type, object>();");
				sb.Append($"\n{i}public {accName}{tparams} {asName}{tparams}(){tconstraints}");
				sb.Append($"\n{i}{{");
				// The implemented closed instantiations are known at generation time; reject any other.
				var asValidCond = string.Join(" && ", closedList.Select(c => $"typeof({accName}{tparams}) != typeof({accName}{c.TypeArguments.GenericArgs()})"));
				sb.Append($"\n{inner}if ({asValidCond})");
				sb.Append($"\n{inner}\tthrow new System.InvalidOperationException(\"This mock does not implement {def.Name} for type argument \" + typeof({accName}{tparams}) + \".\");");
				sb.Append($"\n{inner}if (!{mapField}.TryGetValue(typeof({accName}{tparams}), out var __a))");
				sb.Append($"\n{inner}{{");
				sb.Append($"\n{inner}\t__a = new {accName}{tparams}();");
				sb.Append($"\n{inner}\t{mapField}[typeof({accName}{tparams})] = __a;");
				sb.Append($"\n{inner}}}");
				sb.Append($"\n{inner}return ({accName}{tparams})__a;");
				sb.Append($"\n{i}}}");
				result.Add(new MemberFragment(MemberKind.Raw, "ACC:" + def.QualifiedName(), sb.ToString()));
			}

			// Routing: explicit interface implementation per closed instantiation member.
			foreach (var closed in closedList)
			{
				var asCall = $"{asName}{closed.TypeArguments.GenericArgs()}()";
				var target = closed.QualifiedName();
				foreach (var member in closed.GetMembers())
				{
					switch (member)
					{
						case IPropertySymbol p when !p.IsIndexer:
						{
							var t = p.Type.QualifiedName();
							var hasGet = p.GetMethod != null;
							var hasSet = p.SetMethod != null;
							string text;
							if (hasGet && hasSet)
								text = $"\n{t} {target}.{p.Name} {{ get => {asCall}.{p.Name}; set => {asCall}.{p.Name} = value; }}";
							else if (hasGet)
								text = $"\n{t} {target}.{p.Name} => {asCall}.{p.Name};";
							else
								text = $"\n{t} {target}.{p.Name} {{ set => {asCall}.{p.Name} = value; }}";
							result.Add(new MemberFragment(MemberKind.Raw, "GPX:" + target + "." + p.Name, text));
							break;
						}
						case IEventSymbol e:
						{
							var et = e.Type.QualifiedName();
							var text = $"\nevent {et} {target}.{e.Name} {{ add => {asCall}.{e.Name} += value; remove => {asCall}.{e.Name} -= value; }}";
							result.Add(new MemberFragment(MemberKind.Raw, "GEX:" + target + "." + e.Name, text));
							break;
						}
						case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
						{
							var g = m.TypeParameters.GenericsParams();
							var prmsNoDef = m.MethodParams();
							var ret = m.ReturnTypeName();
							var args = m.MethodArgs();
							// Explicit interface implementations must NOT restate generic constraints (CS0460).
							var text = $"\n{ret} {target}.{m.Name}{g}{prmsNoDef} => {asCall}.{m.Name}{g}{args};";
							result.Add(new MemberFragment(MemberKind.Raw, "GMX:" + target + "." + m.Name + g + m.MethodParamTypes(), text));
							break;
						}
					}
				}
			}
		}

		/// <summary>
		/// Renders the mock members for the interface that a <c>[GenerateInterface]</c> class produces.
		/// The member selection mirrors <see cref="GenerateInterfaceGenerator"/>.
		/// </summary>
		public static MemberFragment[] RenderGenerateInterfaceClass(INamedTypeSymbol typeSymbol)
		{
			var result = new List<MemberFragment>();
			var methods = new Utilities.MethodGroups();
			foreach (var member in Utilities.CollectGenerateInterfaceMembers(typeSymbol))
			{
				AddMember(member, result, methods);
			}
			AppendMethodFragments(result, methods);
			return result.ToArray();
		}
	}
}
