using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MockGenerator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	[Generator]
	public class MockGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(ctx =>
			{
				ctx.AddSource("GenerateViewInterfacesAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateViewInterfacesAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("InputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class InputAttribute : Attribute
						{
							public Type As { get; set; }
						}
					}
					""");
				ctx.AddSource("OutputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class OutputAttribute : Attribute
						{
							public Type As { get; set; }
						}
					}
					""");
				ctx.AddSource("GenerateMockViewAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateMockViewAttribute : Attribute
						{
						}
					}
					""");
			});

			var viewInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateViewInterfacesAttribute",
				static (node, token) => true,
				TransformViewInterfaces).WithTrackingName("MockGenereator.GenerateViewInterfaces");

			context.RegisterSourceOutput(viewInterfaces, static (spc, result) => Replay(spc, result));

			var mockViews = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateMockViewAttribute",
				static (node, token) => true,
				TransformMockView).WithTrackingName("MockGenereator.GenerateMockView");

			context.RegisterSourceOutput(mockViews, static (spc, result) => Replay(spc, result));
		}

		static void Replay(SourceProductionContext context, GenerationResult result)
		{
			foreach (var diag in result.Diagnostics)
			{
				context.ReportDiagnostic(diag.ToDiagnostic());
			}
			foreach (var file in result.Files)
			{
				context.AddSource(file.HintName, file.Content);
			}
		}

		static void Emit(List<SourceFile> files, List<DiagnosticInfo> diagnostics, string hintName, Action<StringBuilder> writer)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			try
			{
				writer(sb);
			}
			catch (Exception e)
			{
				diagnostics.Add(Errors.Unexpected(e));
				sb.AppendFormat("\n#error {0}", e.Message);
			}
			files.Add(new SourceFile(hintName, sb.ToString()));
		}

		static GenerationResult TransformViewInterfaces(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var files = new List<SourceFile>();
			var diagnostics = new List<DiagnosticInfo>();

			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			ValidateAsPlacement(typeSymbol, diagnostics);

			// Diagnostics: [Input] on setter is invalid (setter is implicitly Output).
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IPropertySymbol prop && prop.SetMethod != null
					&& prop.SetMethod.HasAttribute("MockGenerator", "InputAttribute"))
				{
					diagnostics.Add(Errors.InputOnSetter(prop.SetMethod));
				}
			}

			var directInputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: true, output: false);
			var directOutputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: false, output: true);

			// Field bucketing (only fields go through partial class explicit-impl path).
			var @in = new List<IFieldSymbol>();
			var @out = new List<IFieldSymbol>();
			var inout = new List<IFieldSymbol>();
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IFieldSymbol field)
				{
					var fi = field.HasAttribute("MockGenerator", "InputAttribute");
					var fo = field.HasAttribute("MockGenerator", "OutputAttribute");
					if (fi && fo) inout.Add(field);
					else if (fi) @in.Add(field);
					else if (fo) @out.Add(field);
				}
			}

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Input.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[MockGenerator.Input]
					public interface I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}}{{InheritanceClause(directInputIfaces)}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute("MockGenerator", "InputAttribute")) continue;
						var ifield = ResolveFieldInterface(field, input: true, output: false, diagnostics);
						if (ifield != null)
						{
							sb.Append($"\n		{ifield} {field.ToPropertyName()} {{ get; }}");
						}
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.HasInputGetter()) continue;
						var iprop = property.Type.ResolveViewInterfaceName(input: true, output: false);
						sb.Append($"\n		{iprop ?? property.Type.QualifiedName()} {property.Name} {{ get; }}");
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "InputAttribute")) continue;
						sb.Append($"\n		{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute("MockGenerator", "InputAttribute")) continue;
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Output.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[MockGenerator.Output]
					public interface I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{InheritanceClause(directOutputIfaces)}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute("MockGenerator", "OutputAttribute")) continue;
						var ifield = ResolveFieldInterface(field, input: false, output: true, diagnostics);
						if (ifield != null)
						{
							sb.Append($"\n		{ifield} {field.ToPropertyName()} {{ get; }}");
						}
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "OutputAttribute")) continue;
						sb.Append($"\n		{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.IsTracked())
						{
							continue;
						}
						if (property.HasOutputGetter())
						{
							// [Output] getter: composition pull. Setter is suppressed to avoid same-name
							// duplicate declaration in the same interface.
							var iprop = property.Type.ResolveViewInterfaceName(input: false, output: true);
							sb.Append($"\n		{iprop ?? property.Type.QualifiedName()} {property.Name} {{ get; }}");
						}
						else if (property.SetMethod != null)
						{
							sb.Append($"\n		{property.Type.QualifiedName()} {property.Name} {{ set; }}");
						}
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute("MockGenerator", "OutputAttribute"))
						{
							continue;
						}
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			Emit(files, diagnostics, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.I{typeSymbol.Name}.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					public interface I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}},
						I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
					}
				}
				""");
			});

			Emit(files, diagnostics, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.{typeSymbol.Name}.cs", sb =>
			{
				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append($$"""
					namespace {{typeSymbol.ContainingNamespace}}
					{
					""");
				}

				sb.Append($$"""

					public partial class {{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						{{Namespace(typeSymbol)}}.I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var field in @in)
				{
					var ifield = field.Type.ResolveViewInterfaceName(input: true, output: false);
					if (ifield != null)
					{
						sb.Append($"\n		public {ifield} {field.ToPropertyName()} => {field.Name};");
					}
				}

				foreach (var field in @out)
				{
					var ifield = field.Type.ResolveViewInterfaceName(input: false, output: true);
					if (ifield != null)
					{
						sb.Append($"\n		public {ifield} {field.ToPropertyName()} => {field.Name};");
					}
				}

				foreach (var field in inout)
				{
					var inputName = field.Type.ResolveViewInterfaceName(input: true, output: false);
					var outputName = field.Type.ResolveViewInterfaceName(input: false, output: true);
					if (inputName != null && outputName != null)
					{
						var prop = field.ToPropertyName();
						var generics = typeSymbol.TypeParameters.GenericsParams();
						sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {field.Name};");
						sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {field.Name};");
					}
				}

				sb.Append("""

					}
				""");

				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append("""

					}
					""");
				}
			});

			return new GenerationResult(files.ToArray(), diagnostics.ToArray());
		}

		static GenerationResult TransformMockView(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var files = new List<SourceFile>();
			var diagnostics = new List<DiagnosticInfo>();

			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			ValidateAsPlacement(typeSymbol, diagnostics);

			var hasViewIfaces = typeSymbol.HasAttribute("MockGenerator", "GenerateViewInterfacesAttribute");
			var directInputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: true, output: false);
			var directOutputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: false, output: true);
			var inputCollected = Utilities.CollectAttributedInterfaces(typeSymbol, input: true, output: false);
			var outputCollected = Utilities.CollectAttributedInterfaces(typeSymbol, input: false, output: true);

			// Multi-source rule: in two-attr mode, the umbrella ICInput is itself a source, so 1+ direct iface = multi.
			// In alone mode, 2+ direct ifaces = multi.
			bool inputMulti = hasViewIfaces ? directInputIfaces.Count >= 1 : directInputIfaces.Count >= 2;
			bool outputMulti = hasViewIfaces ? directOutputIfaces.Count >= 1 : directOutputIfaces.Count >= 2;

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.Mock{typeSymbol.Name}.cs", sb =>
			{
				sb.Append($$"""
					namespace {{Namespace(typeSymbol)}}
					{
						/// <summary>
						/// <see cref="{{typeSymbol.ToCref()}}"/>
						/// </summary>
						public partial class Mock{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{MockImplementsClause(typeSymbol, hasViewIfaces, directInputIfaces, directOutputIfaces)}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
						{
					""");

				var ownMethods = new Utilities.MethodGroups();
				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						var isInput = field.HasAttribute("MockGenerator", "InputAttribute");
						var isOutput = field.HasAttribute("MockGenerator", "OutputAttribute");
						if (!isInput && !isOutput)
						{
							continue;
						}
						var prop = field.ToPropertyName();
						var generics = typeSymbol.TypeParameters.GenericsParams();
						string inputName = null, outputName = null;
						if (isInput && isOutput)
						{
							inputName = ResolveFieldInterface(field, input: true, output: false, diagnostics);
							outputName = ResolveFieldInterface(field, input: false, output: true, diagnostics);
							if (inputName == null || outputName == null) continue;
						}
						else if (isInput)
						{
							inputName = ResolveFieldInterface(field, input: true, output: false, diagnostics);
							if (inputName == null) continue;
						}
						else
						{
							outputName = ResolveFieldInterface(field, input: false, output: true, diagnostics);
							if (outputName == null) continue;
						}
						var resolvedMock = field.Type.ResolveMockTypeName();
						var mockType = resolvedMock ?? field.Type.QualifiedName();
						var initializer = resolvedMock != null ? $" = new {resolvedMock}();" : "";
						sb.Append($"\n		public {mockType} {prop} {{ get; set; }}{initializer}");
						if (hasViewIfaces)
						{
							if (isInput)
							{
								sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {prop};");
							}
							if (isOutput)
							{
								sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {prop};");
							}
						}
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.IsTracked())
						{
							continue;
						}
						var prop = property.Name;
						var generics = typeSymbol.TypeParameters.GenericsParams();
						var rawType = property.Type.QualifiedName();
						var hasOutputSetter = property.SetMethod != null
							&& property.SetMethod.HasAttribute("MockGenerator", "OutputAttribute");
						if (hasOutputSetter)
						{
							var pascal = char.ToUpper(prop[0]) + prop.Substring(1);
							var camel = char.ToLower(prop[0]) + prop.Substring(1);
							var backing = "_" + camel + "Backing";
							var onSet = "On" + pascal + "Set";
							sb.Append($"\n		public System.Action<{rawType}> {onSet} {{ get; set; }}");
							sb.Append($"\n		private {rawType} {backing};");
							sb.Append($"\n		public {rawType} {prop} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
						}
						else
						{
							sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", rawType, prop);
						}
						if (hasViewIfaces && property.HasInputGetter())
						{
							var inputName = property.Type.ResolveViewInterfaceName(input: true, output: false);
							if (inputName != null && inputName != rawType)
							{
								sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {prop};");
							}
						}
						if (hasViewIfaces && property.HasOutputGetter())
						{
							var outputName = property.Type.ResolveViewInterfaceName(input: false, output: true);
							if (outputName != null && outputName != rawType)
							{
								sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {prop};");
							}
						}
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "InputAttribute")
							&& !method.HasAttribute("MockGenerator", "OutputAttribute"))
						{
							continue;
						}
						ownMethods.Add(method.Name, method);
					}
					else if (member is IEventSymbol @event)
					{
						var isInput = @event.HasAttribute("MockGenerator", "InputAttribute");
						var isOutput = @event.HasAttribute("MockGenerator", "OutputAttribute");
						if (!isInput && !isOutput)
						{
							continue;
						}
						sb.EmitMockEventMember("\t\t", @event);
					}
				}

				ownMethods.EmitAll(sb, "\t\t");

				// Iface-inherited members (multi-interface support).
				var emittedSlots = new HashSet<string>();
				var ifaceMethods = new Utilities.MethodGroups();
				foreach (var iface in inputCollected)
				{
					EmitInterfaceMembers(sb, typeSymbol, iface, inputMulti, emittedSlots, ifaceMethods);
				}
				foreach (var iface in outputCollected)
				{
					EmitInterfaceMembers(sb, typeSymbol, iface, outputMulti, emittedSlots, ifaceMethods);
				}
				ifaceMethods.EmitAll(sb, "\t\t");

				sb.Append("""

						}
					}
					""");
			});

			return new GenerationResult(files.ToArray(), diagnostics.ToArray());
		}

		static string MockImplementsClause(INamedTypeSymbol view, bool hasViewIfaces, List<INamedTypeSymbol> inputIfaces, List<INamedTypeSymbol> outputIfaces)
		{
			if (hasViewIfaces)
			{
				return $" : I{view.Name}{view.TypeParameters.GenericsParams()}";
			}
			var all = new List<INamedTypeSymbol>();
			var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			foreach (var i in inputIfaces) { if (seen.Add(i)) all.Add(i); }
			foreach (var i in outputIfaces) { if (seen.Add(i)) all.Add(i); }
			if (all.Count == 0) return "";
			using var _ = StringBuilderHolder.Get(out var sb);
			sb.Append(" : ");
			for (var i = 0; i < all.Count; ++i)
			{
				if (i != 0) sb.Append(", ");
				sb.Append(all[i].QualifiedName());
			}
			return sb.ToString();
		}

		static void EmitInterfaceMembers(StringBuilder sb, INamedTypeSymbol view, INamedTypeSymbol iface, bool multiSource, HashSet<string> emittedSlots, Utilities.MethodGroups methods)
		{
			var ifaceQ = iface.QualifiedName();
			foreach (var member in iface.GetMembers())
			{
				if (member is IEventSymbol ev)
				{
					var (slotName, isExplicit, _) = DecideSlot(view, iface, ev, multiSource);
					if (!emittedSlots.Add("E:" + slotName)) continue;
					EmitEventSlot(sb, ev, slotName, (isExplicit || multiSource) ? ifaceQ : null);
				}
				else if (member is IPropertySymbol prop)
				{
					var (slotName, isExplicit, prefix) = DecideSlot(view, iface, prop, multiSource);
					if (!emittedSlots.Add("P:" + slotName)) continue;
					EmitPropertySlot(sb, prop, slotName, prefix, (isExplicit || multiSource) ? ifaceQ : null);
				}
				else if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
				{
					var (slotName, isExplicit, _) = DecideSlot(view, iface, m, multiSource);
					methods.Add(slotName, m, (isExplicit || multiSource) ? ifaceQ : null);
				}
			}
		}

		static (string slotName, bool isExplicit, string prefix) DecideSlot(INamedTypeSymbol view, INamedTypeSymbol iface, ISymbol member, bool multiSource)
		{
			var impl = view.FindImplementationForInterfaceMember(member);
			bool isExplicit = false;
			if (impl is IEventSymbol ie) isExplicit = !ie.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			else if (impl is IPropertySymbol ip) isExplicit = !ip.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			else if (impl is IMethodSymbol im) isExplicit = !im.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			bool usePrefix = multiSource || isExplicit;
			string prefix = usePrefix ? $"{iface.Name}_" : "";
			return (prefix + member.Name, isExplicit, prefix);
		}

		static void EmitEventSlot(StringBuilder sb, IEventSymbol e, string slotName, string explicitTarget)
		{
			var eventType = e.Type.QualifiedName();
			var invoke = (e.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
			sb.Append($"\n		public event {eventType} {slotName};");
			if (invoke != null)
			{
				var ret = invoke.ReturnTypeName();
				var returnsVoid = invoke.ReturnsVoid;
				var paramsText = invoke.MethodParams();
				var argsText = invoke.MethodArgs();
				var hasOut = invoke.Parameters.Any(p => p.RefKind == RefKind.Out);
				string body;
				if (hasOut)
				{
					var outInit = string.Concat(invoke.Parameters
						.Where(p => p.RefKind == RefKind.Out)
						.Select(p => $"\n\t\t\t{p.Name} = default;"));
					body = returnsVoid
						? $"\n		{{\n\t\t\tif ({slotName} != null) {{ {slotName}.Invoke{argsText}; return; }}{outInit}\n\t\t}}"
						: $"\n		{{\n\t\t\tif ({slotName} != null) return {slotName}.Invoke{argsText};{outInit}\n\t\t\treturn default({ret});\n\t\t}}";
				}
				else
				{
					body = returnsVoid
						? $" => {slotName}?.Invoke{argsText};"
						: $" => {slotName} != null ? {slotName}.Invoke{argsText} : default({ret});";
				}
				sb.Append($"\n		public {ret} Raise{slotName}{paramsText}{body}");
			}
			if (explicitTarget != null)
			{
				sb.Append($"\n		event {eventType} {explicitTarget}.{e.Name} {{ add => {slotName} += value; remove => {slotName} -= value; }}");
			}
		}

		static void EmitPropertySlot(StringBuilder sb, IPropertySymbol p, string slotName, string prefix, string explicitTarget)
		{
			var t = p.Type.QualifiedName();
			var hasGet = p.GetMethod != null;
			var hasSet = p.SetMethod != null;
			if (hasSet)
			{
				// Mirror On{Name}Set Action pattern for output-setter
				var camel = char.ToLower(slotName[0]) + slotName.Substring(1);
				var backing = "_" + camel + "Backing";
				// Place "On" before the property name, after any interface prefix:
				// prefix "IOut_" + "text" -> "IOut_OnTextSet" (not "OnIOut_textSet").
				var onSet = prefix + "On" + char.ToUpper(p.Name[0]) + p.Name.Substring(1) + "Set";
				sb.Append($"\n		public System.Action<{t}> {onSet} {{ get; set; }}");
				sb.Append($"\n		private {t} {backing};");
				if (hasGet)
				{
					sb.Append($"\n		public {t} {slotName} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
				}
				else
				{
					sb.Append($"\n		public {t} {slotName} {{ set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
				}
			}
			else
			{
				sb.Append($"\n		public {t} {slotName} {{ get; set; }}");
			}
			if (explicitTarget != null)
			{
				if (hasGet && hasSet)
					sb.Append($"\n		{t} {explicitTarget}.{p.Name} {{ get => {slotName}; set => {slotName} = value; }}");
				else if (hasGet)
					sb.Append($"\n		{t} {explicitTarget}.{p.Name} => {slotName};");
				else
					sb.Append($"\n		{t} {explicitTarget}.{p.Name} {{ set => {slotName} = value; }}");
			}
		}

		static string ResolveFieldInterface(IFieldSymbol field, bool input, bool output, List<DiagnosticInfo> diagnostics)
		{
			var r = field.Type.ResolveViewInterface(input, output, field);
			switch (r.Status)
			{
				case Utilities.ResolveStatus.Found:
					return r.Name;
				case Utilities.ResolveStatus.Ambiguous:
					if (input && output) diagnostics.Add(Errors.IsNotInputAndOutput(field));
					else if (input) diagnostics.Add(Errors.AmbiguousInputInterface(field, r.MatchCount));
					else diagnostics.Add(Errors.AmbiguousOutputInterface(field, r.MatchCount));
					return null;
				case Utilities.ResolveStatus.AsNotImplemented:
					diagnostics.Add(Errors.AsTargetNotImplemented(field, r.AsTarget));
					return null;
				case Utilities.ResolveStatus.AsMissingAttribute:
					diagnostics.Add(Errors.AsTargetMissingAttribute(field, r.AsTarget, r.RequiredAttribute));
					return null;
				default:
					if (input && output) diagnostics.Add(Errors.IsNotInputAndOutput(field));
					else if (input) diagnostics.Add(Errors.IsNotInput(field));
					else diagnostics.Add(Errors.IsNotOutput(field));
					return null;
			}
		}

		static string InheritanceClause(List<INamedTypeSymbol> ifaces)
		{
			if (ifaces.Count == 0) return "";
			using var _ = StringBuilderHolder.Get(out var sb);
			sb.Append(" : ");
			for (var i = 0; i < ifaces.Count; ++i)
			{
				if (i != 0) sb.Append(", ");
				sb.Append(ifaces[i].QualifiedName());
			}
			return sb.ToString();
		}

		static void ValidateAsPlacement(INamedTypeSymbol view, List<DiagnosticInfo> diagnostics)
		{
			foreach (var member in view.GetMembers())
			{
				if (member is IFieldSymbol) continue;
				if (member is IPropertySymbol prop)
				{
					if (prop.GetMethod != null && Utilities.HasAsArgument(prop.GetMethod))
						diagnostics.Add(Errors.AsOnNonField(prop.GetMethod));
					if (prop.SetMethod != null && Utilities.HasAsArgument(prop.SetMethod))
						diagnostics.Add(Errors.AsOnNonField(prop.SetMethod));
					continue;
				}
				if (Utilities.HasAsArgument(member))
					diagnostics.Add(Errors.AsOnNonField(member));
			}
			foreach (var iface in view.AllInterfaces)
			{
				if (Utilities.HasAsArgument(iface))
					diagnostics.Add(Errors.AsOnNonField(iface));
			}
		}

		static string Namespace(ISymbol symbol)
		{
			if (symbol.ContainingNamespace.IsGlobalNamespace)
			{
				return "MockView";
			}
			else
			{
				return $"MockView.{symbol.ContainingNamespace}";
			}
		}

	}
}
