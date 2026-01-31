namespace SharpTS.Node.Events;

/// <summary>
/// Node.js-style event emitter for pub/sub event handling.
/// </summary>
public class EventEmitter
{
    private readonly Dictionary<string, List<Delegate>> _listeners = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adds a listener for the specified event.
    /// </summary>
    /// <param name="eventName">The name of the event.</param>
    /// <param name="callback">The callback to invoke when the event is emitted.</param>
    /// <returns>This EventEmitter for chaining.</returns>
    public EventEmitter On(string eventName, Action callback)
    {
        AddListener(eventName, callback);
        return this;
    }

    /// <summary>
    /// Adds a listener for the specified event with one argument.
    /// </summary>
    public EventEmitter On<T>(string eventName, Action<T> callback)
    {
        AddListener(eventName, callback);
        return this;
    }

    /// <summary>
    /// Adds a listener for the specified event with two arguments.
    /// </summary>
    public EventEmitter On<T1, T2>(string eventName, Action<T1, T2> callback)
    {
        AddListener(eventName, callback);
        return this;
    }

    /// <summary>
    /// Adds a one-time listener for the specified event.
    /// </summary>
    public EventEmitter Once(string eventName, Action callback)
    {
        Action wrapper = null!;
        wrapper = () =>
        {
            RemoveListener(eventName, wrapper);
            callback();
        };
        AddListener(eventName, wrapper);
        return this;
    }

    /// <summary>
    /// Adds a one-time listener for the specified event with one argument.
    /// </summary>
    public EventEmitter Once<T>(string eventName, Action<T> callback)
    {
        Action<T> wrapper = null!;
        wrapper = (arg) =>
        {
            RemoveListener(eventName, wrapper);
            callback(arg);
        };
        AddListener(eventName, wrapper);
        return this;
    }

    /// <summary>
    /// Removes a listener from the specified event.
    /// </summary>
    public EventEmitter Off(string eventName, Delegate callback)
    {
        RemoveListener(eventName, callback);
        return this;
    }

    /// <summary>
    /// Removes all listeners for the specified event, or all events if no name is specified.
    /// </summary>
    public EventEmitter RemoveAllListeners(string? eventName = null)
    {
        lock (_lock)
        {
            if (eventName == null)
            {
                _listeners.Clear();
            }
            else if (_listeners.ContainsKey(eventName))
            {
                _listeners.Remove(eventName);
            }
        }
        return this;
    }

    /// <summary>
    /// Emits an event, calling all registered listeners with the provided arguments.
    /// </summary>
    /// <param name="eventName">The name of the event to emit.</param>
    /// <param name="args">Arguments to pass to the listeners.</param>
    /// <returns>True if the event had listeners, false otherwise.</returns>
    public bool Emit(string eventName, params object?[] args)
    {
        List<Delegate>? listenersCopy;
        lock (_lock)
        {
            if (!_listeners.TryGetValue(eventName, out var listeners) || listeners.Count == 0)
                return false;

            // Copy to allow modification during iteration
            listenersCopy = new List<Delegate>(listeners);
        }

        foreach (var listener in listenersCopy)
        {
            try
            {
                listener.DynamicInvoke(args);
            }
            catch (Exception ex)
            {
                // If this is an 'error' event and there are no listeners, rethrow
                if (eventName == "error")
                    throw;

                // For other events, try to emit an 'error' event
                if (eventName != "error" && HasListeners("error"))
                {
                    Emit("error", ex);
                }
                else
                {
                    // No error handler, just log
                    Console.Error.WriteLine($"Error in event listener '{eventName}': {ex}");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the number of listeners for the specified event.
    /// </summary>
    public int ListenerCount(string eventName)
    {
        lock (_lock)
        {
            return _listeners.TryGetValue(eventName, out var listeners) ? listeners.Count : 0;
        }
    }

    /// <summary>
    /// Gets the names of all events that have listeners.
    /// </summary>
    public string[] EventNames()
    {
        lock (_lock)
        {
            return _listeners.Keys.ToArray();
        }
    }

    /// <summary>
    /// Checks if the event has any listeners.
    /// </summary>
    public bool HasListeners(string eventName)
    {
        return ListenerCount(eventName) > 0;
    }

    private void AddListener(string eventName, Delegate callback)
    {
        lock (_lock)
        {
            if (!_listeners.TryGetValue(eventName, out var listeners))
            {
                listeners = new List<Delegate>();
                _listeners[eventName] = listeners;
            }
            listeners.Add(callback);
        }
    }

    private void RemoveListener(string eventName, Delegate callback)
    {
        lock (_lock)
        {
            if (_listeners.TryGetValue(eventName, out var listeners))
            {
                listeners.Remove(callback);
                if (listeners.Count == 0)
                {
                    _listeners.Remove(eventName);
                }
            }
        }
    }
}
