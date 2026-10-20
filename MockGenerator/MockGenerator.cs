using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MockGenerator
{
	[Generator]
	public class MockGenerator : IIncrementalGenerator
	{
		const string AttributeNamespace = Utilities.AttributeNamespace;
		const string UserSideName = Utilities.UserSideName;
		const string AppSideName = Utilities.AppSideName;
		const string UserSideAttributeName = Utilities.UserSideAttributeName;
		const string AppSideAttributeName = Utilities.AppSideAttributeName;

		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(ctx =>
			{
				ctx.AddSource("GenerateViewInterfacesAttribute.cs", $$"""
					#nullable enable
					using System;

					namespace {{AttributeNamespace}}
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateViewInterfacesAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource($"{UserSideAttributeName}.cs", $$"""
					#nullable enable
					using System;

					namespace {{AttributeNamespace}}
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class {{UserSideAttributeName}} : Attribute
						{
							public Type? As { get; set; }
						}
					}
					""");
				ctx.AddSource($"{AppSideAttributeName}.cs", $$"""
					#nullable enable
					using System;

					namespace {{AttributeNamespace}}
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class {{AppSideAttributeName}} : Attribute
						{
							public Type? As { get; set; }
						}
					}
					""");
				ctx.AddSource("GenerateMockViewAttribute.cs", $$"""
					#nullable enable
					using System;

					namespace {{AttributeNamespace}}
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateMockViewAttribute : Attribute
						{
						}
					}
					""");
			});

			var viewInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
				$"{AttributeNamespace}.GenerateViewInterfacesAttribute",
				static (node, token) => true,
				TransformViewInterfaces).WithTrackingName($"{AttributeNamespace}.GenerateViewInterfaces");

			context.RegisterSourceOutput(viewInterfaces, static (spc, result) => Replay(spc, result));

			var mockViews = context.SyntaxProvider.ForAttributeWithMetadataName(
				$"{AttributeNamespace}.GenerateMockViewAttribute",
				static (node, token) => true,
				TransformMockView).WithTrackingName($"{AttributeNamespace}.GenerateMockView");

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
			sb.AppendGeneratedHeader();
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
					&& prop.SetMethod.HasAttribute(AttributeNamespace, UserSideAttributeName))
				{
					diagnostics.Add(Errors.InputOnSetter(prop.SetMethod));
				}
			}

			var directInputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: true, output: false);
			var directOutputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: false, output: true);
			var inheritedInputMembers = InheritedMembers(directInputIfaces);
			var inheritedOutputMembers = InheritedMembers(directOutputIfaces);

			// Field bucketing (only fields go through partial class explicit-impl path).
			var @in = new List<IFieldSymbol>();
			var @out = new List<IFieldSymbol>();
			var inout = new List<IFieldSymbol>();
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IFieldSymbol field)
				{
					var fi = field.HasAttribute(AttributeNamespace, UserSideAttributeName);
					var fo = field.HasAttribute(AttributeNamespace, AppSideAttributeName);
					if (fi && fo) inout.Add(field);
					else if (fi) @in.Add(field);
					else if (fo) @out.Add(field);
				}
			}

			Emit(files, diagnostics, $"{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{UserSideName}.cs", sb =>
			{
				sb.Append($$"""
				namespace {{InterfaceNamespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[{{AttributeNamespace}}.{{UserSideName}}]
					public interface I{{typeSymbol.Name}}{{UserSideName}}{{typeSymbol.TypeParameters.GenericsParams()}}{{InheritanceClause(directInputIfaces)}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute(AttributeNamespace, UserSideAttributeName)) continue;
						var ifield = ResolveFieldInterface(field, input: true, output: false, diagnostics);
						if (ifield == null) continue;
						var fieldProp = field.ToPropertyName();
						var (fieldPlacement, _) = PlaceProperty(inheritedInputMembers, fieldProp, ifield, hasGet: true, hasSet: false);
						if (fieldPlacement == PropertyPlacement.Omit) continue;
						sb.Append($"\n		{(fieldPlacement == PropertyPlacement.Hide ? "new " : "")}{ifield} {fieldProp} {{ get; }}");
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.HasInputGetter()) continue;
						var iprop = property.Type.ResolveViewInterfaceName(input: true, output: false);
						var ipropType = iprop ?? property.Type.QualifiedName();
						var (ipropPlacement, _) = PlaceProperty(inheritedInputMembers, property.Name, ipropType, hasGet: true, hasSet: false);
						if (ipropPlacement == PropertyPlacement.Omit) continue;
						sb.Append($"\n		{property.NullableAttributes()}{(ipropPlacement == PropertyPlacement.Hide ? "new " : "")}{ipropType} {property.Name} {{ get; }}");
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute(AttributeNamespace, UserSideAttributeName)) continue;
						if (inheritedInputMembers.ContainsKey(MethodKey(method))) continue;
						sb.Append($"\n		{method.ReturnNullableAttributes()}{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute(AttributeNamespace, UserSideAttributeName)) continue;
						if (inheritedInputMembers.ContainsKey(EventKey(@event.Name))) continue;
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			Emit(files, diagnostics, $"{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{AppSideName}.cs", sb =>
			{
				sb.Append($$"""
				namespace {{InterfaceNamespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[{{AttributeNamespace}}.{{AppSideName}}]
					public interface I{{typeSymbol.Name}}{{AppSideName}}{{typeSymbol.TypeParameters.GenericsParams()}}{{InheritanceClause(directOutputIfaces)}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute(AttributeNamespace, AppSideAttributeName)) continue;
						var ifield = ResolveFieldInterface(field, input: false, output: true, diagnostics);
						if (ifield == null) continue;
						var fieldProp = field.ToPropertyName();
						var (fieldPlacement, _) = PlaceProperty(inheritedOutputMembers, fieldProp, ifield, hasGet: true, hasSet: false);
						if (fieldPlacement == PropertyPlacement.Omit) continue;
						sb.Append($"\n		{(fieldPlacement == PropertyPlacement.Hide ? "new " : "")}{ifield} {fieldProp} {{ get; }}");
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute(AttributeNamespace, AppSideAttributeName)) continue;
						if (inheritedOutputMembers.ContainsKey(MethodKey(method))) continue;
						sb.Append($"\n		{method.ReturnNullableAttributes()}{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
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
							var ipropType = iprop ?? property.Type.QualifiedName();
							var (ipropPlacement, ipropBase) = PlaceProperty(inheritedOutputMembers, property.Name, ipropType, hasGet: true, hasSet: false);
							if (ipropPlacement == PropertyPlacement.Omit) continue;
							var ipropAccessors = ipropPlacement == PropertyPlacement.Hide
								? HiddenPropertyAccessors(ipropBase, ipropType, hasGet: true, hasSet: false)
								: "{ get; }";
							sb.Append($"\n		{property.NullableAttributes()}{(ipropPlacement == PropertyPlacement.Hide ? "new " : "")}{ipropType} {property.Name} {ipropAccessors}");
						}
						else if (property.SetMethod != null)
						{
							var setType = property.Type.QualifiedName();
							var (setPlacement, setBase) = PlaceProperty(inheritedOutputMembers, property.Name, setType, hasGet: false, hasSet: true);
							if (setPlacement == PropertyPlacement.Omit) continue;
							var setAccessors = setPlacement == PropertyPlacement.Hide
								? HiddenPropertyAccessors(setBase, setType, hasGet: false, hasSet: true)
								: "{ set; }";
							sb.Append($"\n		{property.NullableAttributes()}{(setPlacement == PropertyPlacement.Hide ? "new " : "")}{setType} {property.Name} {setAccessors}");
						}
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute(AttributeNamespace, AppSideAttributeName))
						{
							continue;
						}
						if (inheritedOutputMembers.ContainsKey(EventKey(@event.Name))) continue;
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
				namespace {{InterfaceNamespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					public interface I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						I{{typeSymbol.Name}}{{UserSideName}}{{typeSymbol.TypeParameters.GenericsParams()}},
						I{{typeSymbol.Name}}{{AppSideName}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
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
						global::{{InterfaceNamespace(typeSymbol)}}.I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
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
						var (inPlacement, inBase) = PlaceProperty(inheritedInputMembers, prop, inputName, hasGet: true, hasSet: false);
						var (outPlacement, outBase) = PlaceProperty(inheritedOutputMembers, prop, outputName, hasGet: true, hasSet: false);
						var inTarget = inPlacement == PropertyPlacement.Omit
							? inBase.ContainingType.QualifiedName()
							: $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{UserSideName}{generics}";
						var outTarget = outPlacement == PropertyPlacement.Omit
							? outBase.ContainingType.QualifiedName()
							: $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{AppSideName}{generics}";
						sb.Append($"\n		{inputName} {inTarget}.{prop} => {field.Name};");
						sb.Append($"\n		{outputName} {outTarget}.{prop} => {field.Name};");
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

			var hasViewIfaces = typeSymbol.HasAttribute(AttributeNamespace, "GenerateViewInterfacesAttribute");
			var directInputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: true, output: false);
			var directOutputIfaces = Utilities.DirectAttributedInterfaces(typeSymbol, input: false, output: true);
			var inheritedInputMembers = InheritedMembers(directInputIfaces);
			var inheritedOutputMembers = InheritedMembers(directOutputIfaces);
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
				var emittedSlots = new HashSet<string>();
				var satisfiedBaseMembers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						var isInput = field.HasAttribute(AttributeNamespace, UserSideAttributeName);
						var isOutput = field.HasAttribute(AttributeNamespace, AppSideAttributeName);
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
						var fieldIsNullable = field.Type.NullableAnnotation == NullableAnnotation.Annotated;
						var mockType = resolvedMock != null
							? (fieldIsNullable ? resolvedMock + "?" : resolvedMock)
							: field.Type.QualifiedName();
						var initializer = fieldIsNullable ? "" : (resolvedMock != null ? $" = new {resolvedMock}();" : field.Type.AutoPropertyInitializer());
						sb.Append($"\n		public {mockType} {prop} {{ get; set; }}{initializer}");
						emittedSlots.Add("P:" + prop);
						if (hasViewIfaces)
						{
							if (isInput)
							{
								var (inPlacement, inBase) = PlaceProperty(inheritedInputMembers, prop, inputName, hasGet: true, hasSet: false);
								var inTarget = $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{UserSideName}{generics}";
								if (inPlacement == PropertyPlacement.Omit)
								{
									inTarget = inBase.ContainingType.QualifiedName();
									satisfiedBaseMembers.Add(inBase);
								}
								sb.Append($"\n		{inputName} {inTarget}.{prop} => {prop};");
							}
							if (isOutput)
							{
								var (outPlacement, outBase) = PlaceProperty(inheritedOutputMembers, prop, outputName, hasGet: true, hasSet: false);
								var outTarget = $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{AppSideName}{generics}";
								if (outPlacement == PropertyPlacement.Omit)
								{
									outTarget = outBase.ContainingType.QualifiedName();
									satisfiedBaseMembers.Add(outBase);
								}
								sb.Append($"\n		{outputName} {outTarget}.{prop} => {prop};");
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
						var attrs = property.NullableAttributes();
						var hasOutputSetter = property.SetMethod != null
							&& property.SetMethod.HasAttribute(AttributeNamespace, AppSideAttributeName);
						if (hasOutputSetter)
						{
							var pascal = char.ToUpper(prop[0]) + prop.Substring(1);
							var camel = char.ToLower(prop[0]) + prop.Substring(1);
							var backing = "_" + camel + "Backing";
							var onSet = "On" + pascal + "Set";
							sb.Append($"\n		public System.Action<{property.SetterValueTypeName()}>? {onSet} {{ get; set; }}");
							sb.Append($"\n		{attrs}private {rawType} {backing}{property.BackingInitializer()};");
							sb.Append($"\n		{attrs}public {rawType} {prop} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
						}
						else
						{
							sb.Append($"\n		{attrs}public {rawType} {prop} {{ get; set; }}{property.SlotInitializer()}");
						}
						emittedSlots.Add("P:" + prop);
						if (hasViewIfaces && property.HasInputGetter())
						{
							var inputName = property.Type.ResolveViewInterfaceName(input: true, output: false);
							var (inPlacement, inBase) = PlaceProperty(inheritedInputMembers, prop, inputName ?? rawType, hasGet: true, hasSet: false);
							if (inPlacement == PropertyPlacement.Omit) satisfiedBaseMembers.Add(inBase);
							if (inputName != null && inputName != rawType)
							{
								var inTarget = inPlacement == PropertyPlacement.Omit
									? inBase.ContainingType.QualifiedName()
									: $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{UserSideName}{generics}";
								sb.Append($"\n		{inputName} {inTarget}.{prop} => {prop};");
							}
						}
						if (hasViewIfaces && property.HasOutputGetter())
						{
							var outputName = property.Type.ResolveViewInterfaceName(input: false, output: true);
							var (outPlacement, outBase) = PlaceProperty(inheritedOutputMembers, prop, outputName ?? rawType, hasGet: true, hasSet: false);
							if (outPlacement == PropertyPlacement.Omit) satisfiedBaseMembers.Add(outBase);
							if (outputName != null && outputName != rawType)
							{
								var outTarget = outPlacement == PropertyPlacement.Omit
									? outBase.ContainingType.QualifiedName()
									: $"global::{InterfaceNamespace(typeSymbol)}.I{typeSymbol.Name}{AppSideName}{generics}";
								sb.Append($"\n		{outputName} {outTarget}.{prop} => {prop};");
							}
						}
						if (hasViewIfaces && !property.HasOutputGetter() && property.SetMethod != null)
						{
							var (setPlacement, setBase) = PlaceProperty(inheritedOutputMembers, prop, rawType, hasGet: false, hasSet: true);
							if (setPlacement == PropertyPlacement.Omit) satisfiedBaseMembers.Add(setBase);
						}
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute(AttributeNamespace, UserSideAttributeName)
							&& !method.HasAttribute(AttributeNamespace, AppSideAttributeName))
						{
							continue;
						}
						ownMethods.Add(method.Name, method);
					}
					else if (member is IEventSymbol @event)
					{
						var isInput = @event.HasAttribute(AttributeNamespace, UserSideAttributeName);
						var isOutput = @event.HasAttribute(AttributeNamespace, AppSideAttributeName);
						if (!isInput && !isOutput)
						{
							continue;
						}
						sb.EmitMockEventMember("\t\t", @event);
						emittedSlots.Add("E:" + @event.Name);
					}
				}

				ownMethods.EmitAll(sb, "\t\t");

				// Iface-inherited members (multi-interface support).
				// Non-generic interfaces use the flat (optionally prefixed) slot scheme. Generic interfaces
				// use the As{Iface}<...>() accessor scheme so that distinct closed type arguments (including
				// nested generics, any arity) never collide.
				var ifaceMethods = new Utilities.MethodGroups();
				var genericDefs = new List<INamedTypeSymbol>();
				var genericClosed = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
				void Route(INamedTypeSymbol iface, bool multi)
				{
					if (iface.OriginalDefinition.TypeParameters.Length > 0)
					{
						var def = iface.OriginalDefinition;
						if (!genericClosed.TryGetValue(def, out var list))
						{
							list = new List<INamedTypeSymbol>();
							genericClosed[def] = list;
							genericDefs.Add(def);
						}
						if (!list.Any(x => SymbolEqualityComparer.Default.Equals(x, iface)))
						{
							list.Add(iface);
						}
					}
					else
					{
						EmitInterfaceMembers(sb, typeSymbol, iface, multi, emittedSlots, ifaceMethods, satisfiedBaseMembers);
					}
				}
				foreach (var iface in inputCollected) Route(iface, inputMulti);
				foreach (var iface in outputCollected) Route(iface, outputMulti);
				ifaceMethods.EmitAll(sb, "\t\t");
				foreach (var def in genericDefs)
				{
					EmitGenericInterfaceAccessor(sb, typeSymbol, def, genericClosed[def], emittedSlots);
				}

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
				return $" : global::{InterfaceNamespace(view)}.I{view.Name}{view.TypeParameters.GenericsParams()}";
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

		static void EmitInterfaceMembers(StringBuilder sb, INamedTypeSymbol view, INamedTypeSymbol iface, bool multiSource, HashSet<string> emittedSlots, Utilities.MethodGroups methods, HashSet<ISymbol> satisfiedBaseMembers)
		{
			var ifaceQ = iface.QualifiedName();
			foreach (var member in iface.GetMembers())
			{
				if (satisfiedBaseMembers.Contains(member)) continue;
				if (member is IEventSymbol ev)
				{
					var (slotName, isExplicit, prefix) = DecideSlot(view, iface, ev, multiSource);
					if (!emittedSlots.Add("E:" + slotName)) continue;
					EmitEventSlot(sb, ev, slotName, prefix, (isExplicit || multiSource) ? ifaceQ : null);
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
					var impl = view.FindImplementationForInterfaceMember(m) as IMethodSymbol;
					methods.Add(slotName, m, (isExplicit || multiSource) ? ifaceQ : null, impl);
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

		/// <summary>
		/// Emits an event slot and its Raise helper. The helper keeps the interface prefix in front, mirroring
		/// the accessor form the member would take if the mock exposed one object per interface:
		/// prefix "IDialog_" + "OnClick" -&gt; "IDialog_RaiseOnClick" (not "RaiseIDialog_OnClick").
		/// </summary>
		static void EmitEventSlot(StringBuilder sb, IEventSymbol e, string slotName, string prefix, string explicitTarget)
		{
			sb.EmitMockEventSlot("\t\t", e, slotName, prefix, explicitTarget);
		}

		/// <summary>
		/// Emits a property slot: a setter becomes the <c>On{Name}Set</c> Action + backing pattern, placing "On"
		/// before the property name, after any interface prefix (prefix "IOut_" + "text" -&gt; "IOut_OnTextSet").
		/// </summary>
		static void EmitPropertySlot(StringBuilder sb, IPropertySymbol p, string slotName, string prefix, string explicitTarget)
		{
			sb.EmitMockPropertySlot("\t\t", p, slotName, prefix, explicitTarget);
		}

		/// <summary>
		/// Emits the accessor scheme for one generic interface definition (e.g. <c>IFoo&lt;T&gt;</c>):
		/// a nested <c>{Iface}Accessor&lt;...&gt;</c> holding the mock storage, an <c>As{Iface}&lt;...&gt;()</c>
		/// entry that resolves the accessor for a closed type argument set via <c>typeof</c>, and one routing
		/// member per implemented closed instantiation (public mirror when the View implements implicitly,
		/// explicit interface implementation when explicitly). Type arguments — any arity, any nesting — ride
		/// on the method type parameters, so no identifier mangling is needed.
		/// </summary>
		static void EmitGenericInterfaceAccessor(StringBuilder sb, INamedTypeSymbol view, INamedTypeSymbol def, List<INamedTypeSymbol> closedList, HashSet<string> emittedSlots)
		{
			var accName = def.Name + "Accessor";
			var asName = "As" + def.Name;
			var tparams = def.TypeParameters.GenericsParams();
			var tconstraints = def.TypeParameters.GenericsConstraints();
			var mapField = "_" + char.ToLower(def.Name[0]) + def.Name.Substring(1) + "AccessorMap";

			// Accessor type + As<...>() entry: once per generic interface definition.
			if (emittedSlots.Add("ACC:" + def.QualifiedName()))
			{
				sb.Append($"\n		public sealed class {accName}{tparams}{tconstraints}");
				sb.Append("\n		{");
				var accMethods = new Utilities.MethodGroups();
				foreach (var member in def.GetMembers())
				{
					if (member is IPropertySymbol prop)
					{
						EmitAccessorProperty(sb, prop);
					}
					else if (member is IEventSymbol ev)
					{
						sb.EmitMockEventMember("\t\t\t", ev);
					}
					else if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
					{
						accMethods.Add(m.Name, m);
					}
				}
				accMethods.EmitAll(sb, "\t\t\t");
				sb.Append("\n		}");

				sb.Append($"\n		private readonly System.Collections.Generic.Dictionary<System.Type, object> {mapField} = new System.Collections.Generic.Dictionary<System.Type, object>();");
				sb.Append($"\n		public {accName}{tparams} {asName}{tparams}(){tconstraints}");
				sb.Append("\n		{");
				// The implemented closed instantiations are known at generation time; reject any other.
				var asValidCond = string.Join(" && ", closedList.Select(c => $"typeof({accName}{tparams}) != typeof({accName}{c.TypeArguments.GenericArgs()})"));
				sb.Append($"\n			if ({asValidCond})");
				sb.Append($"\n				throw new System.InvalidOperationException(\"This mock does not implement {def.Name} for type argument \" + typeof({accName}{tparams}) + \".\");");
				sb.Append($"\n			if (!{mapField}.TryGetValue(typeof({accName}{tparams}), out var __a))");
				sb.Append("\n			{");
				sb.Append($"\n				__a = new {accName}{tparams}();");
				sb.Append($"\n				{mapField}[typeof({accName}{tparams})] = __a;");
				sb.Append("\n			}");
				sb.Append($"\n			return ({accName}{tparams})__a;");
				sb.Append("\n		}");
			}

			// Routing: one member per implemented closed instantiation, into its accessor.
			foreach (var closed in closedList)
			{
				var asCall = $"{asName}{closed.TypeArguments.GenericArgs()}()";
				foreach (var member in closed.GetMembers())
				{
					EmitAccessorRouting(sb, view, closed, member, asCall, emittedSlots);
				}
			}
		}

		/// <summary>
		/// Emits one member inside a generic interface accessor. Mirrors <see cref="EmitPropertySlot"/>'s
		/// convention: a setter becomes the <c>On{Name}Set</c> Action + backing pattern, a getter-only
		/// property becomes auto get/set storage. The member name is used verbatim (no prefix), because the
		/// accessor type itself is the disambiguator.
		/// </summary>
		static void EmitAccessorProperty(StringBuilder sb, IPropertySymbol p)
		{
			sb.EmitMockPropertySlot("\t\t\t", p, p.Name, "", null);
		}

		/// <summary>
		/// Emits the Mock-side member that satisfies one closed-interface member by forwarding to its accessor.
		/// A public mirror when the View implements the member implicitly; an explicit interface implementation
		/// when explicitly. Duplicate public signatures (the same View member shared by several interface members)
		/// are emitted once.
		/// </summary>
		static void EmitAccessorRouting(StringBuilder sb, INamedTypeSymbol view, INamedTypeSymbol closed, ISymbol member, string asCall, HashSet<string> emittedSlots)
		{
			var impl = view.FindImplementationForInterfaceMember(member);
			bool isExplicit = false;
			if (impl is IPropertySymbol ip) isExplicit = !ip.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			else if (impl is IEventSymbol ie) isExplicit = !ie.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			else if (impl is IMethodSymbol im) isExplicit = !im.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
			var target = closed.QualifiedName();

			if (member is IPropertySymbol p)
			{
				var t = p.Type.QualifiedName();
				var attrs = p.NullableAttributes();
				var hasGet = p.GetMethod != null;
				var hasSet = p.SetMethod != null;
				if (!isExplicit)
				{
					if (!emittedSlots.Add("GP:" + p.Name)) return;
					if (hasGet && hasSet)
						sb.Append($"\n		{attrs}public {t} {p.Name} {{ get => {asCall}.{p.Name}; set => {asCall}.{p.Name} = value; }}");
					else if (hasGet)
						sb.Append($"\n		{attrs}public {t} {p.Name} {{ get => {asCall}.{p.Name}; }}");
					else
						sb.Append($"\n		{attrs}public {t} {p.Name} {{ set => {asCall}.{p.Name} = value; }}");
				}
				else
				{
					if (!emittedSlots.Add("GPX:" + target + "." + p.Name)) return;
					if (hasGet && hasSet)
						sb.Append($"\n		{attrs}{t} {target}.{p.Name} {{ get => {asCall}.{p.Name}; set => {asCall}.{p.Name} = value; }}");
					else if (hasGet)
						sb.Append($"\n		{attrs}{t} {target}.{p.Name} => {asCall}.{p.Name};");
					else
						sb.Append($"\n		{attrs}{t} {target}.{p.Name} {{ set => {asCall}.{p.Name} = value; }}");
				}
			}
			else if (member is IEventSymbol e)
			{
				var et = e.Type.QualifiedName();
				if (!isExplicit)
				{
					if (!emittedSlots.Add("GE:" + e.Name)) return;
					sb.Append($"\n		public event {et} {e.Name} {{ add => {asCall}.{e.Name} += value; remove => {asCall}.{e.Name} -= value; }}");
				}
				else
				{
					if (!emittedSlots.Add("GEX:" + target + "." + e.Name)) return;
					sb.Append($"\n		event {et} {target}.{e.Name} {{ add => {asCall}.{e.Name} += value; remove => {asCall}.{e.Name} -= value; }}");
				}
			}
			else if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
			{
				var ret = m.ReturnTypeName();
				var retAttrs = m.ReturnNullableAttributes();
				var g = m.TypeParameters.GenericsParams();
				var c = m.TypeParameters.GenericsConstraints();
				var prmsNoDef = m.MethodParams();
				var args = m.MethodArgs();
				var call = $"{asCall}.{m.Name}{g}{args}";
				if (!isExplicit)
				{
					if (!emittedSlots.Add("GM:" + m.Name + g + m.MethodParamTypes())) return;
					var prms = m.MethodParams(withDefaults: true);
					sb.Append($"\n		{retAttrs}public {ret} {m.Name}{g}{prms}{c} => {call};");
				}
				else
				{
					// Explicit interface implementations must NOT restate generic constraints (CS0460).
					if (!emittedSlots.Add("GMX:" + target + "." + m.Name + g + m.MethodParamTypes())) return;
					sb.Append($"\n		{retAttrs}{ret} {target}.{m.Name}{g}{prmsNoDef}{m.GenericsDefaultConstraints()} => {call};");
				}
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

		/// <summary>
		/// Keys (see <see cref="MethodKey"/>, <see cref="PropertyKey"/>, <see cref="EventKey"/>) of every member
		/// declared by <paramref name="ifaces"/> or any of their base interfaces. A generated view interface that
		/// inherits these must not re-declare them: the member is already inherited, and re-declaring it only
		/// hides it (CS0108). The value is the declared member that was found first for the key.
		/// </summary>
		static Dictionary<string, ISymbol> InheritedMembers(List<INamedTypeSymbol> ifaces)
		{
			var keys = new Dictionary<string, ISymbol>();
			var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			foreach (var root in ifaces)
			{
				foreach (var iface in new[] { root }.Concat(root.AllInterfaces))
				{
					if (!seen.Add(iface)) continue;
					foreach (var member in iface.GetMembers())
					{
						switch (member)
						{
							case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
								if (!keys.ContainsKey(MethodKey(m))) keys[MethodKey(m)] = m;
								break;
							case IPropertySymbol p:
								if (!keys.ContainsKey(PropertyKey(p.Name))) keys[PropertyKey(p.Name)] = p;
								break;
							case IEventSymbol e:
								if (!keys.ContainsKey(EventKey(e.Name))) keys[EventKey(e.Name)] = e;
								break;
						}
					}
				}
			}
			return keys;
		}

		/// <summary>
		/// How a view property (or a projected field) relates to a same-named property of the inherited
		/// interfaces. <c>Omit</c>: the base declares it with the same type and every accessor the view
		/// offers, so the generated interface inherits it and the view's implementation satisfies the base
		/// member (<c>DeclaringInterface</c> is where it lives). <c>Hide</c>: the base has the name but a
		/// different type or fewer accessors, so the generated interface declares it with <c>new</c>.
		/// <c>Declare</c>: no base member of that name.
		/// </summary>
		enum PropertyPlacement { Declare, Hide, Omit }

		static (PropertyPlacement Placement, IPropertySymbol BaseProperty) PlaceProperty(
			Dictionary<string, ISymbol> inherited, string name, string typeFqn, bool hasGet, bool hasSet)
		{
			if (!inherited.TryGetValue(PropertyKey(name), out var s) || s is not IPropertySymbol bp)
				return (PropertyPlacement.Declare, null);
			if (bp.Type.QualifiedName() != typeFqn) return (PropertyPlacement.Hide, bp);
			if ((hasGet && bp.GetMethod == null) || (hasSet && bp.SetMethod == null)) return (PropertyPlacement.Hide, bp);
			return (PropertyPlacement.Omit, bp);
		}

		/// <summary>
		/// The accessor list for a property that hides <paramref name="baseProperty"/>: when the type is the same
		/// the union of the base's and the view's accessors, otherwise only the view's.
		/// </summary>
		static string HiddenPropertyAccessors(IPropertySymbol baseProperty, string typeFqn, bool hasGet, bool hasSet)
		{
			if (baseProperty.Type.QualifiedName() == typeFqn)
			{
				hasGet |= baseProperty.GetMethod != null;
				hasSet |= baseProperty.SetMethod != null;
			}
			return hasGet && hasSet ? "{ get; set; }" : hasGet ? "{ get; }" : "{ set; }";
		}

		static string MethodKey(IMethodSymbol m) => "M:" + m.Name + "`" + m.Arity + m.MethodParamTypes();
		static string PropertyKey(string name) => "P:" + name;
		static string EventKey(string name) => "E:" + name;

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

		static string InterfaceNamespace(ISymbol symbol)
		{
			if (symbol.ContainingNamespace.IsGlobalNamespace)
			{
				return "Generated";
			}
			else
			{
				return $"{symbol.ContainingNamespace}.Generated";
			}
		}
	}
}
