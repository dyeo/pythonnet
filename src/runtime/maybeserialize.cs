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

        public bool Valid => m_item != null;

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


    [Serializable]
    internal struct MaybeType : ISerializable
    {
        public static implicit operator MaybeType (Type ob) => new MaybeType(ob);

        string m_name;
        Type m_type;
        public string DeletedMessage
        {
            get
            {
                return $"The .NET Type {m_name} no longer exists";
            }
        }
        public Type Value
        {
            get
            {
                if (m_type == null)
                {
                    throw new SerializationException(DeletedMessage);
                }
                return m_type;
            }
        }
        public override string ToString()
        {
            return (m_type != null ? m_type.ToString() : $"missing type: {m_name}") + Valid.ToString();
        }
        public string Name {get{return m_name;}}
        public bool Valid => m_type != null;

        public MaybeType(Type tp)
        {
            m_type = tp;
            m_name = tp.AssemblyQualifiedName;
        }

        private MaybeType(SerializationInfo info, StreamingContext context)
        {
            m_name = (string)info.GetValue("n", typeof(string));
            m_type = Type.GetType(m_name, throwOnError:false);
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("n", m_name);
        }
    }

    [Serializable]
    internal struct MaybeMethod<T> : ISerializable where T: MethodBase//, MethodInfo, ConstructorInfo
    {
        public static implicit operator T (MaybeMethod<T> self) => (T)self.Value;

        public static implicit operator MaybeMethod<T> (MethodBase ob) => new MaybeMethod<T>((T)ob);

        string m_name;
        MethodBase m_info;

        // As seen in ClassManager.GetClassInfo
        const BindingFlags k_flags = BindingFlags.Static |
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic;
        public T Value
        {
            get
            {
                if (m_info == null)
                {
                    throw new SerializationException($"The .NET {typeof(T)} {m_name} no longer exists");
                }
                return (T)m_info;
            }
        }
        public T UnsafeValue { get { return (T)m_info; } }
        
        public override string ToString()
        {
            return (m_info != null ? m_info.ToString() : $"missing method info: {m_name}");
        }
        public string Name {get{return m_name;}}
        public bool Valid => m_info != null;

        public MaybeMethod(T mi)
        {
            m_info = mi;
            m_name = mi?.ToString();
        }

        internal MaybeMethod(SerializationInfo info, StreamingContext context)
        {
            m_name = info.GetString("s");
            m_info = null;
            try
            {
                var tp = Type.GetType(info.GetString("t"), throwOnError:false);
                if (tp != null)
                {
                    var field_name = info.GetString("f");
                    var param = (string[])info.GetValue("p", typeof(string[]));
                    Type[] types = new Type[param.Length];
                    
                        for (int i = 0; i < param.Length; i++)
                        {
                            types[i] = Type.GetType(param[i]);
                        }
                        m_info = tp.GetMethod(field_name, k_flags, binder:null, types:types, modifiers:null);
                        if (m_info == null && m_name.Contains(".ctor"))
                        {
                            m_info = tp.GetConstructor(k_flags, binder:null, types:types, modifiers:null);
                        }
                }
            }
            catch
            {
            }
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("s", m_name);
            if (Valid)
            {
                info.AddValue("f", m_info.Name);
                info.AddValue("t", m_info.ReflectedType.AssemblyQualifiedName);
                var p = m_info.GetParameters();
                string[] types = new string[p.Length];
                for (int i = 0; i < p.Length; i++)
                {
                    types[i] = p[i].ParameterType.AssemblyQualifiedName;
                }
                info.AddValue("p", types, typeof(string[]));
            }
        }
    }

}
