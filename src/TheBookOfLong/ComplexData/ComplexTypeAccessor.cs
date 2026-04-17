using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes;

namespace TheBookOfLong;

internal static class ComplexTypeAccessor
{
    internal static object CreateObjectInstance(Type type)
    {
        if (type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        if (TryCreateObjectInstanceViaConstructors(type, out object? instance, out Exception? lastConstructorException))
        {
            return instance!;
        }

        if (IsIl2CppReferenceType(type))
        {
            throw new InvalidOperationException(
                $"Could not create IL2CPP object instance for '{type.FullName}'.",
                lastConstructorException);
        }

        return RuntimeHelpers.GetUninitializedObject(type);
    }

    internal static long GetObjectIdentity(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        return value is Il2CppObjectBase il2CppObject
            ? il2CppObject.Pointer.ToInt64()
            : RuntimeHelpers.GetHashCode(value);
    }

    private static bool TryCreateObjectInstanceViaConstructors(Type type, out object? instance, out Exception? lastException)
    {
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Array.Sort(constructors, CompareConstructors);

        lastException = null;
        for (int i = 0; i < constructors.Length; i += 1)
        {
            ConstructorInfo constructor = constructors[i];
            if (!IsUsableObjectConstructor(constructor))
            {
                continue;
            }

            try
            {
                object?[] arguments = BuildConstructorArguments(constructor);
                instance = constructor.Invoke(arguments);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                lastException = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        instance = null;
        return false;
    }

    private static int CompareConstructors(ConstructorInfo left, ConstructorInfo right)
    {
        int leftScore = GetConstructorSortScore(left);
        int rightScore = GetConstructorSortScore(right);
        int scoreComparison = leftScore.CompareTo(rightScore);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        return right.IsPublic.CompareTo(left.IsPublic);
    }

    private static int GetConstructorSortScore(ConstructorInfo constructor)
    {
        int parameterCount = constructor.GetParameters().Length;
        return IsUsableObjectConstructor(constructor) ? parameterCount : 1000 + parameterCount;
    }

    private static bool IsUsableObjectConstructor(ConstructorInfo constructor)
    {
        ParameterInfo[] parameters = constructor.GetParameters();
        for (int i = 0; i < parameters.Length; i += 1)
        {
            ParameterInfo parameter = parameters[i];
            Type parameterType = parameter.ParameterType;
            if (parameter.IsOut
                || parameterType.IsByRef
                || parameterType.IsPointer
                || parameterType == typeof(IntPtr)
                || parameterType == typeof(UIntPtr))
            {
                return false;
            }
        }

        return true;
    }

    private static object?[] BuildConstructorArguments(ConstructorInfo constructor)
    {
        ParameterInfo[] parameters = constructor.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i += 1)
        {
            ParameterInfo parameter = parameters[i];
            if (parameter.HasDefaultValue)
            {
                arguments[i] = parameter.DefaultValue is DBNull
                    ? CreateDefaultConstructorArgument(parameter.ParameterType)
                    : parameter.DefaultValue;
                continue;
            }

            arguments[i] = CreateDefaultConstructorArgument(parameter.ParameterType);
        }

        return arguments;
    }

    private static object? CreateDefaultConstructorArgument(Type parameterType)
    {
        Type effectiveType = parameterType.IsByRef
            ? parameterType.GetElementType() ?? parameterType
            : parameterType;

        if (effectiveType == typeof(string))
        {
            return string.Empty;
        }

        if (Nullable.GetUnderlyingType(effectiveType) is not null)
        {
            return null;
        }

        return effectiveType.IsValueType
            ? Activator.CreateInstance(effectiveType)
            : null;
    }

    private static bool IsIl2CppReferenceType(Type type)
    {
        return !type.IsValueType && typeof(Il2CppObjectBase).IsAssignableFrom(type);
    }

    internal static Dictionary<string, ComplexPatchableMember> GetPatchableMembers(Type type)
    {
        Dictionary<string, ComplexPatchableMember> members = new(StringComparer.OrdinalIgnoreCase);

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

                members[property.Name] = new ComplexPatchableMember(
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

                members[field.Name] = new ComplexPatchableMember(
                    field.Name,
                    field.FieldType,
                    target => field.GetValue(target),
                    (target, value) => field.SetValue(target, value));
            }
        }

        return members;
    }

    internal static object CreateListInstance(Type listType, Type elementType)
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

    internal static void AddCollectionItem(object collection, object? item)
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

    internal static void SetCollectionItem(object collection, int index, object? item)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? itemProperty = collection.GetType().GetProperty("Item", Flags, null, null, new[] { typeof(int) }, null);
        if (itemProperty is not null && itemProperty.CanWrite)
        {
            itemProperty.SetValue(collection, item, new object[] { index });
            return;
        }

        MethodInfo? setter = collection.GetType().GetMethod("set_Item", Flags, null, new[] { typeof(int), item?.GetType() ?? typeof(object) }, null);
        if (setter is not null)
        {
            setter.Invoke(collection, new[] { (object)index, item });
            return;
        }

        throw new InvalidOperationException($"Collection type '{collection.GetType().FullName}' does not expose a usable indexed setter.");
    }

    internal static void RemoveCollectionItemAt(object collection, int index)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? removeAtMethod = collection.GetType().GetMethod("RemoveAt", Flags, null, new[] { typeof(int) }, null);
        if (removeAtMethod is null)
        {
            throw new InvalidOperationException($"Collection type '{collection.GetType().FullName}' does not expose a usable RemoveAt method.");
        }

        removeAtMethod.Invoke(collection, new object[] { index });
    }

    internal static List<object?> EnumerateCollection(object collection)
    {
        if (collection is IEnumerable enumerable)
        {
            List<object?> enumerableItems = new();
            foreach (object? item in enumerable)
            {
                enumerableItems.Add(item);
            }

            return enumerableItems;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? countProperty = collection.GetType().GetProperty("Count", Flags);
        PropertyInfo? itemProperty = collection.GetType().GetProperty("Item", Flags, null, null, new[] { typeof(int) }, null);
        if (countProperty is null || itemProperty is null || !countProperty.CanRead || !itemProperty.CanRead)
        {
            throw new InvalidOperationException($"Value '{collection.GetType().FullName}' is not enumerable.");
        }

        object? rawCount = countProperty.GetValue(collection);
        int count = rawCount is null ? 0 : Convert.ToInt32(rawCount);

        List<object?> items = new(count);
        for (int i = 0; i < count; i += 1)
        {
            items.Add(itemProperty.GetValue(collection, new object[] { i }));
        }

        return items;
    }

    internal static Type? ResolveCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType();
        }

        return TryResolveCollectionElementType(collectionType, out Type? elementType)
            ? elementType
            : null;
    }

    internal static bool TryResolveCollectionElementType(Type collectionType, out Type? elementType)
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

    internal static string? GetNameValue(object target)
    {
        return TryGetMemberValue(target, "name", out object? value) ? value?.ToString() : null;
    }

    internal static string GetRequiredStringProperty(JsonElement element, string propertyName, string filePath, string jsonPath)
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

    internal static Type? GetMemberType(Type targetType, string memberName)
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

    internal static bool TryGetMemberValue(object target, string memberName, out object? value)
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

    internal static void SetMemberValue(object target, string memberName, object? value)
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
