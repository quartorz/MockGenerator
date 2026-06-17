using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

#nullable enable

namespace MockGenereator
{
	[Generator]
	public class GenerateMockForGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(static ctx =>
			{
				ctx.AddSource("GenerateMockForAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
						internal sealed class GenerateMockForAttribute : Attribute
						{
							public GenerateMockForAttribute(Type interfaceType) { InterfaceType = interfaceType; }
							public Type InterfaceType { get; }
						}
					}
					""");
			});

			var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateMockForAttribute",
				static (node, _) => true,
				TransformTarget).WithTrackingName("MockGenereator.GenerateMockForGenerator");

			// [GenerateInterface] produces interfaces in the same compilation, which are invisible to
			// this generator's view. Collect them as value-equatable models so unresolved interface
			// arguments of [GenerateMockFor] can be matched against them.
			var interfaceModels = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateInterfaceAttribute",
				static (node, _) => true,
				TransformInterfaceModel).WithTrackingName("MockGenereator.GenerateMockForGenerator.Interfaces")
				.Collect();

			context.RegisterSourceOutput(targets.Combine(interfaceModels), static (spc, pair) =>
			{
				var (target, models) = pair;
				if (target == null) return;
				Emit(spc, target, models);
			});
		}

		static MockTarget? TransformTarget(GeneratorAttributeSyntaxContext context, CancellationToken token)
		{
			var targetSymbol = (INamedTypeSymbol)context.TargetSymbol;
			var ns = targetSymbol.ContainingNamespace.IsGlobalNamespace ? null : targetSymbol.ContainingNamespace.ToString();
			var className = targetSymbol.Name;
			var generics = targetSymbol.IsGenericType ? targetSymbol.TypeParameters.GenericsParams() : "";
			var constraints = targetSymbol.IsGenericType ? targetSymbol.TypeParameters.GenericsConstraints() : "";

			var resolved = new List<ResolvedInterface>();
			var unresolved = new List<UnresolvedInterface>();

			foreach (var attr in context.Attributes)
			{
				if (attr.AttributeClass?.Name != "GenerateMockForAttribute") continue;
				if (attr.ConstructorArguments.Length == 0) continue;
				if (attr.ConstructorArguments[0].Value is not INamedTypeSymbol it) continue;

				if (it.TypeKind == TypeKind.Interface)
				{
					resolved.Add(new ResolvedInterface(it.QualifiedName(), MockMembers.RenderInterface(it)));
				}
				else if (it.TypeKind == TypeKind.Error)
				{
					var nsHint = it.ContainingNamespace != null && !it.ContainingNamespace.IsGlobalNamespace
						? it.ContainingNamespace.ToString()
						: null;
					var location = LocationInfo.From(attr.ApplicationSyntaxReference?.GetSyntax(token)?.GetLocation());
					unresolved.Add(new UnresolvedInterface(it.Name, nsHint, location));
				}
			}

			if (resolved.Count == 0 && unresolved.Count == 0) return null;

			return new MockTarget(ns, className, generics, constraints, resolved.ToArray(), unresolved.ToArray());
		}

		static InterfaceMockModel TransformInterfaceModel(GeneratorAttributeSyntaxContext context, CancellationToken token)
		{
			var typeSymbol = (INamedTypeSymbol)context.TargetSymbol;
			var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToString();

			string? interfaceName = null;
			foreach (var attr in context.Attributes)
			{
				if (attr.AttributeClass?.Name != "GenerateInterfaceAttribute") continue;
				if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is string s && !string.IsNullOrWhiteSpace(s))
				{
					interfaceName = s;
				}
				break;
			}
			interfaceName ??= "I" + typeSymbol.Name;

			var generics = typeSymbol.TypeParameters.GenericsParams();
			return new InterfaceMockModel(ns, interfaceName, generics, MockMembers.RenderGenerateInterfaceClass(typeSymbol));
		}

		static void Emit(SourceProductionContext spc, MockTarget target, ImmutableArray<InterfaceMockModel> models)
		{
			var baseNames = new List<string>();
			var members = new List<MemberFragment>();

			foreach (var resolved in target.Resolved)
			{
				baseNames.Add(resolved.QualifiedName);
				members.AddRange(resolved.Members);
			}

			foreach (var unresolved in target.Unresolved)
			{
				var matches = models.Where(m => m.InterfaceName == unresolved.SimpleName).ToList();
				if (matches.Count > 1 && unresolved.NamespaceHint != null)
				{
					var narrowed = matches.Where(m => m.Namespace == unresolved.NamespaceHint).ToList();
					if (narrowed.Count > 0) matches = narrowed;
				}

				if (matches.Count == 0)
				{
					spc.ReportDiagnostic(Errors.CannotResolveMockInterface(unresolved.SimpleName, unresolved.Location).ToDiagnostic());
					continue;
				}
				if (matches.Count > 1)
				{
					spc.ReportDiagnostic(Errors.AmbiguousMockInterface(unresolved.SimpleName, unresolved.Location).ToDiagnostic());
					continue;
				}

				var model = matches[0];
				baseNames.Add(model.QualifiedName);
				members.AddRange(model.Members);
			}

			if (baseNames.Count == 0) return;

			using var _ = StringBuilderHolder.Get(out var sb);

			var indent = target.Namespace != null ? "\t" : "";
			var memberIndent = indent + "\t";
			var baseList = string.Join(", ", baseNames);

			if (target.Namespace != null)
			{
				sb.Append($$"""
					namespace {{target.Namespace}}
					{
						partial class {{target.ClassName}}{{target.Generics}} : {{baseList}}{{target.Constraints}}
						{
					""");
			}
			else
			{
				sb.Append($$"""
					partial class {{target.ClassName}}{{target.Generics}} : {{baseList}}{{target.Constraints}}
					{
					""");
			}

			var seenMethodFunc = new HashSet<string>();
			var seenProperty = new HashSet<string>();
			var seenEvent = new HashSet<string>();
			var seenRaw = new HashSet<string>();

			foreach (var fragment in members)
			{
				switch (fragment.Kind)
				{
					case MemberKind.Property:
						if (!seenProperty.Add(fragment.Key)) continue;
						break;
					case MemberKind.Method:
						if (!seenMethodFunc.Add(fragment.Key))
						{
							sb.Append($"\n{memberIndent}#warning GenerateMockFor: overload for '{fragment.Key}' was skipped (Func name '{fragment.Key}Func' already used)");
							continue;
						}
						break;
					case MemberKind.Event:
						if (!seenEvent.Add(fragment.Key)) continue;
						break;
					case MemberKind.Raw:
						if (!seenRaw.Add(fragment.Key)) continue;
						break;
				}

				// Fragments are rendered with no indentation; re-indent to the member level.
				sb.Append(fragment.Text.Replace("\n", "\n" + memberIndent));
			}

			if (target.Namespace != null)
			{
				sb.Append("\n\t}\n}\n");
			}
			else
			{
				sb.Append("\n}\n");
			}

			var hint = (target.Namespace == null ? "" : target.Namespace + ".") + target.ClassName + ".GenerateMockFor.cs";
			spc.AddSource(hint, sb.ToString());
		}
	}
}
