#define NO_SERIALIZATION
// #define SINGLE_STREAM
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;

namespace Python.Runtime
{
    /// <summary>
    /// A MaybeSerialize&lt;T&gt; delays errors from serialization and
    /// deserialization until the item is used.
    ///
    /// Python for .NET uses this in the C# reloading architecture.
    /// If e.g. a class member was renamed when reloading, references to the
    /// old field will be invalid, but the rest of the system will still work.
    /// Code that tries to use the old field will receive an exception.
    ///
    /// Assumption: the item being wrapped by MaybeSerialize will never be null.
    /// </summary>
    [Serializable]
    internal struct MaybeSerialize<T> : ISerializable where T : class
    {

        public static implicit operator T (MaybeSerialize<T> self) => self.Value;

        public static implicit operator MaybeSerialize<T> (T ob) => new MaybeSerialize<T>(ob);

        /// <summary>
        /// The item being wrapped.
        ///
        /// If this is null, that means we failed to serialize or deserialize it.
        /// </summary>
        private T m_item;

        /// <summary>
        /// A string useful for debugging the error.
        ///
        /// This is null if m_item deserialized properly.
        /// Otherwise, it will be derived off of m_item.ToString() when we
        /// serialized.
        /// </summary>
        private string m_name;

        /// <summary>
        /// Store an item in such a way that it can be deserialized.
        ///
        /// It must not be null.
        /// </summary>
        public MaybeSerialize(T item)
        {
            if (item == null)
            {
                throw new System.ArgumentNullException("Trying to store a null");
            }
            m_item = item;
            m_name = null;
        }

        /// <summary>
        /// Get the underlying deserialized value, or throw an exception
        /// if deserialiation failed.
        /// </summary>
        public T Value
        {
            get
            {
                if (m_item == null)
                {
                    // extra debug in case it gets caught by a catch {...}
                    Console.WriteLine("MaybeSerialize throwing on null");
                    throw new SerializationException($"The .NET object underlying {m_name} no longer exists");
                }
                return m_item;
            }
        }

        /// <summary>
        /// Get a printable name.
        /// </summary>
        public override string ToString()
        {
            if (m_item == null)
            {
                return $"(missing {m_name})";
            }
            else
            {
                return m_item.ToString();
            }
        }

        /// <summary>
        /// Implements ISerializable
        /// </summary>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
#if NO_SERIALIZATION
            // store the name, plus the string representation of the item
            // so that when it's deserialized into a Dictionary<>, it's unique
            info.AddValue("n", m_name+ToString());
#elif SINGLE_STREAM
            if (m_item == null)
            {
                // Save the name; this failed to reload in a previous
                // generation but we still need to remember what it was.
                info.AddValue("n", m_name);
            }
            else
            {
                // Try to save the item. If it fails, too bad.
                try
                {
                    info.AddValue("i", m_item);
                }
                catch(SerializationException _)
                {
                }

                // Also save the name in case the item doesn't deserialize
                info.AddValue("n", m_item.ToString());
            }
#else
            if (m_item == null)
            {
                info.AddValue("n", m_name);
            }
            else
            {
                // Serialize in a silly way. TODO optimize.
                var formatter = new BinaryFormatter();
                using (var ms = new MemoryStream())
                {
                    formatter.Serialize(ms, m_item);
                    info.AddValue("i", ms.ToArray());
                }
                // Also save the name in case the info doesn't deserialize
                info.AddValue("n", m_item.ToString());
            }
#endif
        }

        /// <summary>
        /// Implements ISerializable
        /// </summary>
        private MaybeSerialize(SerializationInfo info, StreamingContext context)
        {
#if NO_SERIALIZATION
            m_item = null;
            m_name = info.GetString("n");
#elif SINGLE_STREAM
            try
            {
                // Try to deserialize the item. It might fail, or it might
                // have already failed so there just isn't an "i" to find.
                m_item = (T)info.GetValue("i", typeof(T));
                m_name = null;
            }
            catch (SerializationException _)
            {
                // Getting the item failed, so get the name.
                m_item = null;
                m_name = info.GetString("n");
            }
            //------------------------------------------
#else
            try
            {
                // Try to deserialize the item. It might fail, or it might
                // have already failed so there just isn't an "i" to find.
                m_item = (T)info.GetValue("i", typeof(T));
                m_name = null;
                var serialized = (byte[])info.GetValue("i", typeof(byte[]));
                var formatter = new BinaryFormatter();
                using (var ms = new MemoryStream(serialized))
                {
                    m_item = (T)formatter.Deserialize(ms);
                }
            }
            // catch (SerializationException _)
            catch (Exception e)
            {
                // Getting the item failed, so get the name.
                m_item = null;
                m_name = info.GetString("n");
                Console.WriteLine($"oopsie woopsie:{m_name} @ {e.Message}:: {e.StackTrace}");
                // Console.WriteLine($"failed to deserializing {typeof(T)}::{m_name}");
            }

            m_name = (m_item != null) ? null : info.GetString("n");

            // Console.WriteLine(System.Environment.StackTrace);
            // Console.WriteLine($"Done {typeof(T)}::{info.GetString("n")}");
#endif
        }
    }
}
