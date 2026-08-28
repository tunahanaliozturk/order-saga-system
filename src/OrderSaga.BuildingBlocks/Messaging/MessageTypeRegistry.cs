using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderSaga.Contracts;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>
/// Maps between a contract's CLR type and the name stored in the outbox.
/// </summary>
/// <remarks>
/// Storing an assembly-qualified type name would tie every row in the outbox to the assembly layout of the
/// service that wrote it, so renaming a project or moving a type would break messages already staged but
/// not yet published. The short type name is stable across all of that, and the registry fails loudly at
/// startup if two contracts ever collide on one.
/// </remarks>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<string, Type> _byName;

    /// <summary>Builds the registry from every contract in the shared assembly.</summary>
    public MessageTypeRegistry()
    {
        _byName = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (Type type in typeof(ISagaMessage).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(ISagaMessage).IsAssignableFrom(type))
            {
                continue;
            }

            if (!_byName.TryAdd(type.Name, type))
            {
                throw new InvalidOperationException(
                    $"Two message contracts share the name '{type.Name}'. Outbox rows would be ambiguous.");
            }
        }
    }

    /// <summary>Serialisation settings. Shared so what the outbox writes is what the relay reads.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>The name a message is stored under.</summary>
    /// <param name="messageType">Contract type.</param>
    public static string NameOf(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        return messageType.Name;
    }

    /// <summary>Resolves a stored name back to its contract type.</summary>
    /// <param name="name">Stored message type name.</param>
    /// <param name="messageType">The contract type.</param>
    public bool TryResolve(string name, [NotNullWhen(true)] out Type? messageType) =>
        _byName.TryGetValue(name, out messageType);

    /// <summary>Every contract the registry knows. Used by the startup self-check and by tests.</summary>
    public IReadOnlyCollection<Type> KnownTypes => _byName.Values;

    /// <summary>Deserialises a stored payload.</summary>
    /// <param name="messageType">Contract type.</param>
    /// <param name="payload">Stored JSON.</param>
    public static object Deserialize(Type messageType, string payload) =>
        JsonSerializer.Deserialize(payload, messageType, SerializerOptions)
        ?? throw new InvalidOperationException($"Outbox payload for {messageType.Name} deserialised to null.");

    /// <summary>
    /// Serialises a message for storage, using its runtime type rather than the declared one.
    /// </summary>
    /// <remarks>
    /// A caller holding an <see cref="ISagaMessage"/> reference would otherwise serialise only the two
    /// properties the interface declares, and the message would arrive stripped of everything that
    /// mattered.
    /// </remarks>
    /// <param name="message">The message.</param>
    public static string Serialize(ISagaMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), SerializerOptions);
    }
}
