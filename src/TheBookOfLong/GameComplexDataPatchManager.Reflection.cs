using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
    private static object CreateObjectInstance(Type type)
    {
        if (type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        ConstructorInfo? constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        if (constructor is not null)
        {
            return constructor.Invoke(Array.Empty<object>());
        }

        return RuntimeHelpers.GetUninitializedObject(type);
    }

    /// <summary>
    /// 只暴露可安全写入的实例成员，避免在递归补丁时把 IL2CPP 桥接层和只读元数据也算进去。
    /// </summary>
    private static Dictionary<string, PatchableMember> GetPatchableMembers(Type type)
    {
        Dictionary<string, PatchableMember> members = new(StringComparer.OrdinalIgnoreCase);

        for (Type? currentType = type;
             currentType is not null && currentType != typeof(object);
             currentType = currentType.BaseType)
        {
            string? namespaceName = currentType.Namespace;
            if (!string.IsNullOrEmpty(namespaceName)
                && namespaceName.StartsWith("Il2CppInterop.Runtime", StringComparison.Ordinal))
            {
                break;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

            foreach (PropertyInfo property in currentType.GetProperties(Flags))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 || members.ContainsKey(property.Name))
                {
                    continue;
                }

                members[property.Name] = new PatchableMember(
                    property.Name,
                    property.PropertyType,
                    target => property.GetValue(target),
                    (target, value) => property.SetValue(target, value));
            }

            foreach (FieldInfo field in currentType.GetFields(Flags))
            {
                if (field.IsInitOnly || members.ContainsKey(field.Name))
                {
                    continue;
                }

                members[field.Name] = new PatchableMember(
                    field.Name,
                    field.FieldType,
                    target => field.GetValue(target),
                    (target, value) => field.SetValue(target, value));
            }
        }

        return members;
    }

    private static object CreateListInstance(Type listType, Type elementType)
    {
        Type concreteType = listType;
        if (listType.IsArray)
        {
            throw new InvalidOperationException($"Array type '{listType.FullName}' is not supported for complex data patches.");
        }

        if (listType.IsInterface || listType.IsAbstract)
        {
            concreteType = typeof(List<>).MakeGenericType(elementType);
        }

        return CreateObjectInstance(concreteType);
    }

    private static void AddCollectionItem(object collection, object? item)
    {
        MethodInfo? addMethod = null;
        foreach (MethodInfo candidate in collection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (candidate.Name != "Add")
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            if (item is null || parameters[0].ParameterType.IsInstanceOfType(item))
            {
                addMethod = candidate;
                break;
            }
        }

        if (addMethod is null)
        {
            throw new InvalidOperationException($"Collection type '{collection.GetType().FullName}' does not expose a usable Add method.");
        }

        addMethod.Invoke(collection, new[] { item });
    }

    private static List<object?> EnumerateCollection(object collection)
    {
        if (collection is not IEnumerable enumerable)
        {
            throw new InvalidOperationException($"Value '{collection.GetType().FullName}' is not enumerable.");
        }

        List<object?> items = new();
        foreach (object? item in enumerable)
        {
            items.Add(item);
        }

        return items;
    }

    private static Type? ResolveCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType();
        }

        return TryResolveCollectionElementType(collectionType, out Type? elementType)
            ? elementType
            : null;
    }

    private static bool TryResolveCollectionElementType(Type collectionType, out Type? elementType)
    {
        elementType = null;

        if (collectionType == typeof(string))
        {
            return false;
        }

        if (collectionType.IsGenericType)
        {
            Type[] genericArguments = collectionType.GetGenericArguments();
            if (genericArguments.Length == 1)
            {
                elementType = genericArguments[0];
                return true;
            }
        }

        Type[] interfaces = collectionType.GetInterfaces();
        for (int i = 0; i < interfaces.Length; i += 1)
        {
            Type interfaceType = interfaces[i];
            if (!interfaceType.IsGenericType || interfaceType.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            elementType = interfaceType.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    private static string? GetNameValue(object target)
    {
        return TryGetMemberValue(target, "name", out object? value) ? value?.ToString() : null;
    }

    private static string GetRequiredStringProperty(JsonElement element, string propertyName, string filePath, string jsonPath)
    {
        if (element.TryGetProperty(propertyName, out JsonElement propertyValue) && propertyValue.ValueKind == JsonValueKind.String)
        {
            string text = propertyValue.GetString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException($"Patch file '{filePath}' is missing a non-empty string property '{propertyName}' at '{jsonPath}'.");
    }

    private static Type? GetMemberType(Type targetType, string memberName)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = targetType.GetProperty(memberName, Flags);
        if (property is not null)
        {
            return property.PropertyType;
        }

        FieldInfo? field = targetType.GetField(memberName, Flags);
        return field?.FieldType;
    }

    private static bool TryGetMemberValue(object target, string memberName, out object? value)
    {
        Type type = target.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = type.GetProperty(memberName, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0 && property.CanRead)
        {
            value = property.GetValue(target);
            return true;
        }

        FieldInfo? field = type.GetField(memberName, Flags);
        if (field is not null)
        {
            value = field.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    private static void SetMemberValue(object target, string memberName, object? value)
    {
        Type type = target.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = type.GetProperty(memberName, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0 && property.CanWrite)
        {
            property.SetValue(target, value);
            return;
        }

        FieldInfo? field = type.GetField(memberName, Flags);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        throw new InvalidOperationException($"Could not set member '{memberName}' on '{type.FullName}'.");
    }
}
