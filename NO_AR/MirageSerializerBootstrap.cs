using System;
using System.Linq;
using System.Reflection;
using Mirage.Serialization;

namespace NO_AR;

// Mirage.CodeGen emits Mirage.GeneratedNetworkCode.InitReadWriters() into the woven
// plugin assembly and marks it RuntimeInitializeOnLoadMethod. BepInEx loads this DLL
// dynamically, so Unity does not reliably execute that generated initialiser.
// Install only this plugin's generated serializers instead of invoking the complete
// generated initialiser, which would also re-register Nuclear Option's built-in types.
internal static class MirageSerializerBootstrap
{
    private static bool _initialised;
    internal static bool Ready { get; private set; }

    internal static bool Initialise()
    {
        if (_initialised)
            return Ready;

        _initialised = true;
        try
        {
            var generatedType = typeof(Plugin).Assembly.GetType("Mirage.GeneratedNetworkCode", throwOnError: false);
            if (generatedType == null)
            {
                Plugin.Logger.LogError("Mirage generated network code was not found in the plugin DLL. " +
                                    "Run Mirage.CodeGen/Weaver on the final assembly.");
                return false;
            }

            Ready = RegisterGeneratedMessage<FuelTransferMessage>(generatedType) &&
                    RegisterGeneratedMessage<ProviderFuelDrainMessage>(generatedType) &&
                    RegisterGeneratedMessage<AirResupplyHudStateMessage>(generatedType) &&
                    RegisterGeneratedMessage<AirResupplyMapStatusMessage>(generatedType) &&
                    RegisterGeneratedMessage<ProviderPolicyRequestMessage>(generatedType) &&
                    RegisterGeneratedMessage<ProviderPolicyMessage>(generatedType) &&
                    RegisterGeneratedMessage<ProviderPreferenceMessage>(generatedType) &&
                    RegisterGeneratedMessage<ProviderRoleAssignmentMessage>(generatedType);

            return Ready;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Failed to initialise Mirage serializers.\n" + ex);
            Ready = false;
            return false;
        }
    }

    private static bool RegisterGeneratedMessage<T>(Type generatedType) where T : struct
    {
        try
        {
            var methods = generatedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var writeMethod = methods.FirstOrDefault(method =>
            {
                if (method.ReturnType != typeof(void))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 2 && parameters[0].ParameterType == typeof(NetworkWriter) && parameters[1].ParameterType == typeof(T);
            });

            var readMethod = methods.FirstOrDefault(method =>
            {
                if (method.ReturnType != typeof(T))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(NetworkReader);
            });

            if (writeMethod == null || readMethod == null)
            {
                Plugin.Logger.LogError($"Mirage generated serializer missing for {typeof(T).FullName}. " +
                                    $"Writer: {(writeMethod != null)}, Reader: {(readMethod != null)}");
                return false;
            }

            if (!InstallDelegate(typeof(Writer<T>), "Write", writeMethod) ||
                !InstallDelegate(typeof(Reader<T>), "Read", readMethod))
            {
                Plugin.Logger.LogError($"Could not install Mirage generated serializer for {typeof(T).FullName}.");
                return false;
            }

            MessagePacker.RegisterMessage<T>();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Failed to register Mirage message {typeof(T).FullName}.\n{ex}");
            return false;
        }
    }

    private static bool InstallDelegate(Type holderType, string memberName, MethodInfo generatedMethod)
    {
        var field = holderType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType))
        {
            field.SetValue(null, Delegate.CreateDelegate(field.FieldType, generatedMethod));
            return true;
        }

        var property = holderType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property?.SetMethod == null || !typeof(Delegate).IsAssignableFrom(property.PropertyType))
            return false;
        
        property.SetValue(null, Delegate.CreateDelegate(property.PropertyType, generatedMethod));
        return true;
    }
}