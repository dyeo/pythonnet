#define NEW
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Python.Runtime
{
    /// <summary>
    /// Implements the "import hook" used to integrate Python with the CLR.
    /// </summary>
    internal static class ImportHook
    {
        private static IntPtr py_import;
        private static CLRModule root;
        private static MethodWrapper hook;
        private static IntPtr py_clr_module;
        static BorrowedReference ClrModuleReference => new BorrowedReference(py_clr_module);

        private const string LoaderCode = @"
import importlib.abc
import sys

class DotNetLoader(importlib.abc.Loader):

    @classmethod
    def exec_module(klass, mod):
        # This method needs to exist.
        pass

    @classmethod
    def create_module(klass, spec):
        import clr
        return clr._load_clr_module(spec)

class DotNetFinder(importlib.abc.MetaPathFinder):

    @classmethod
    def find_spec(klass, fullname, paths=None, target=None): 
        import clr
        if clr._available_namespaces and fullname in clr._available_namespaces:
            return importlib.machinery.ModuleSpec(fullname, DotNetLoader(), is_package=True)
        return None
            ";
        const string availableNsKey = "_available_namespaces";

        /// <summary>
        /// Initialize just the __import__ hook itself.
        /// </summary>
        static void InitImport()
        {
            // We replace the built-in Python __import__ with our own: first
            // look in CLR modules, then if we don't find any call the default
            // Python __import__.
            IntPtr builtins = Runtime.GetBuiltins();
            py_import = Runtime.PyObject_GetAttr(builtins, PyIdentifier.__import__);
            PythonException.ThrowIfIsNull(py_import);
#if OLD
            hook = new MethodWrapper(typeof(ImportHook), "__import__", "TernaryFunc");
            int res = Runtime.PyObject_SetAttr(builtins, PyIdentifier.__import__, hook.ptr);
            PythonException.ThrowIfIsNotZero(res);
#endif
            Runtime.XDecref(builtins);
        }

        /// <summary>
        /// Restore the __import__ hook.
        /// </summary>
        static void RestoreImport()
        {
#if OLD
            IntPtr builtins = Runtime.GetBuiltins();

            IntPtr existing = Runtime.PyObject_GetAttr(builtins, PyIdentifier.__import__);
            Runtime.XDecref(existing);
            if (existing != hook.ptr)
            {
                throw new NotSupportedException("Unable to restore original __import__.");
            }

            int res = Runtime.PyObject_SetAttr(builtins, PyIdentifier.__import__, py_import);
            PythonException.ThrowIfIsNotZero(res);
#endif
            Runtime.XDecref(py_import);
            py_import = IntPtr.Zero;
#if OLD
            hook.Release();
            hook = null;

            Runtime.XDecref(builtins);
#endif
        }

        /// <summary>
        /// Initialization performed on startup of the Python runtime.
        /// </summary>
        internal static unsafe void Initialize()
        {
            InitImport();

            // Initialize the clr module and tell Python about it.
            root = new CLRModule();

            // create a python module with the same methods as the clr module-like object
            py_clr_module = Runtime.PyModule_New("clr").DangerousMoveToPointer();

            // both dicts are borrowed references
            BorrowedReference mod_dict = Runtime.PyModule_GetDict(ClrModuleReference);
            using var clr_dict = Runtime.PyObject_GenericGetDict(root.ObjectReference);

            Runtime.PyDict_Update(mod_dict, clr_dict);
            BorrowedReference dict = Runtime.PyImport_GetModuleDict();
            Runtime.PyDict_SetItemString(dict, "CLR", ClrModuleReference);
            Runtime.PyDict_SetItemString(dict, "clr", ClrModuleReference);
#if NEW
            SetupNamespaceTracking();
            SetupImportHook();
#endif
        }

        static void SetupImportHook()
        {
            // Create the import hook module
            var import_hook_module = Runtime.PyModule_New("clr.loader");

            // Run the python code to create the module's classes.
            var builtins = Runtime.PyEval_GetBuiltins();
            var exec = Runtime.PyDict_GetItemString(builtins, "exec");
            using var args = NewReference.DangerousFromPointer(Runtime.PyTuple_New(2));

            var codeStr = Runtime.PyString_FromString(LoaderCode);
            Runtime.PyTuple_SetItem(args.DangerousGetAddress(), 0, codeStr);
            // PyTuple_SetItem steals a reference, mod_dict is borrowed.
            var mod_dict = Runtime.PyModule_GetDict(import_hook_module);
            Runtime.XIncref(mod_dict.DangerousGetAddress());
            Runtime.PyTuple_SetItem(args.DangerousGetAddress(), 1, mod_dict.DangerousGetAddress());
            Runtime.PyObject_Call(exec.DangerousGetAddress(), args.DangerousGetAddress(), IntPtr.Zero);

            // Set as a sub-module of clr.
            Runtime.XIncref(import_hook_module.DangerousGetAddress());
            if(Runtime.PyModule_AddObject(ClrModuleReference, "loader", import_hook_module) != 0)
            {
                Runtime.XDecref(import_hook_module.DangerousGetAddress());
                throw new PythonException();
            }

            // Finally, add the hook to the meta path
            // var finder_inst = Runtime.PyDict_GetItemString(mod_dict, "finder_inst").DangerousGetAddressOrNull();
            var findercls = Runtime.PyDict_GetItemString(mod_dict, "DotNetFinder");
            var finderCtorArgs = Runtime.PyTuple_New(0);
            var finder_inst = Runtime.PyObject_CallObject(findercls.DangerousGetAddress(), finderCtorArgs);
            Runtime.XDecref(finderCtorArgs);
            var metapath = Runtime.PySys_GetObject("meta_path");
            Runtime.PyList_Append(metapath, finder_inst);
        }


        /// <summary>
        /// Cleanup resources upon shutdown of the Python runtime.
        /// </summary>
        internal static void Shutdown()
        {
            if (Runtime.Py_IsInitialized() == 0)
            {
                return;
            }

            RestoreImport();
#if NEW
            TeardownNameSpaceTracking();
#endif
            Runtime.XDecref(py_clr_module);
            py_clr_module = IntPtr.Zero;

            Runtime.XDecref(root.pyHandle);
            root = null;
            CLRModule.Reset();
        }

        internal static void SaveRuntimeData(RuntimeDataStorage storage)
        {
            // Increment the reference counts here so that the objects don't
            // get freed in Shutdown.
            Runtime.XIncref(py_clr_module);
            Runtime.XIncref(root.pyHandle);
            storage.AddValue("py_clr_module", py_clr_module);
            storage.AddValue("root", root.pyHandle);
        }

        internal static void RestoreRuntimeData(RuntimeDataStorage storage)
        {
            InitImport();
            storage.GetValue("py_clr_module", out py_clr_module);
            var rootHandle = storage.GetValue<IntPtr>("root");
            root = (CLRModule)ManagedType.GetManagedObject(rootHandle);
        }

        static void SetupNamespaceTracking()
        {
            var newset = Runtime.PySet_New(new BorrowedReference(IntPtr.Zero));
            try
            {
                foreach (var ns in AssemblyManager.GetNamespaces())
                {
                    var pyNs = Runtime.PyString_FromString(ns);
                    try
                    {
                        if (Runtime.PySet_Add(newset, new BorrowedReference(pyNs)) != 0)
                        {
                            throw new PythonException();
                        }
                    }
                    finally
                    {
                        Runtime.XDecref(pyNs);
                    }
                }

                if (Runtime.PyDict_SetItemString(root.dict, availableNsKey, newset.DangerousGetAddress()) != 0)
                {
                    throw new PythonException();
                }
            }
            finally
            {
                newset.Dispose();
            }

            AssemblyManager.namespaceAdded += OnNamespaceAdded;
            PythonEngine.AddShutdownHandler(() => AssemblyManager.namespaceAdded -= OnNamespaceAdded);
        }

        /// <summary>
        /// Removes the set of available namespaces from the clr module and 
        /// removes the callback on the OnNamespaceAdded event.
        /// </summary>
        static void TeardownNameSpaceTracking()
        {
            AssemblyManager.namespaceAdded -= OnNamespaceAdded;
            // If the C# runtime isn't loaded, then there are no namespaces available
            Runtime.PyDict_SetItemString(root.dict, availableNsKey, Runtime.PyNone);
        }

        static void OnNamespaceAdded(string name)
        {
            using (Py.GIL())
            {
                var pyNs = Runtime.PyString_FromString(name);
                try
                {
                    var nsSet = Runtime.PyDict_GetItemString(new BorrowedReference(root.dict), availableNsKey);
                    if (!nsSet.IsNull || nsSet.DangerousGetAddress() != Runtime.PyNone)
                    {
                        if (Runtime.PySet_Add(nsSet, new BorrowedReference(pyNs)) != 0)
                        {
                            throw new PythonException();
                        }
                    }
                }
                finally
                {
                    Runtime.XDecref(pyNs);
                }
            }
        }

        /// <summary>
        /// Because we use a proxy module for the clr module, we somtimes need
        /// to force the py_clr_module to sync with the actual clr module's dict.
        /// </summary>
        internal static void UpdateCLRModuleDict()
        {
            root.InitializePreload();

            // update the module dictionary with the contents of the root dictionary
            root.LoadNames();
            BorrowedReference py_mod_dict = Runtime.PyModule_GetDict(ClrModuleReference);
            using var clr_dict = Runtime.PyObject_GenericGetDict(root.ObjectReference);

            Runtime.PyDict_Update(py_mod_dict, clr_dict);
        }

        /// <summary>
        /// Return the clr python module (new reference)
        /// </summary>
        public static unsafe NewReference GetCLRModule(BorrowedReference fromList = default)
        {
            root.InitializePreload();

            // update the module dictionary with the contents of the root dictionary
            root.LoadNames();
            BorrowedReference py_mod_dict = Runtime.PyModule_GetDict(ClrModuleReference);
            using (var clr_dict = Runtime.PyObject_GenericGetDict(root.ObjectReference))
            {
                Runtime.PyDict_Update(py_mod_dict, clr_dict);
            }

            // find any items from the from list and get them from the root if they're not
            // already in the module dictionary
            if (fromList != null)
            {
                if (Runtime.PyTuple_Check(fromList))
                {
                    using var mod_dict = new PyDict(py_mod_dict);
                    using var from = new PyTuple(fromList);
                    foreach (PyObject item in from)
                    {
                        if (mod_dict.HasKey(item))
                        {
                            continue;
                        }

                        var s = item.AsManagedObject(typeof(string)) as string;
                        if (s == null)
                        {
                            continue;
                        }

                        ManagedType attr = root.GetAttribute(s, true);
                        if (attr == null)
                        {
                            continue;
                        }

                        Runtime.XIncref(attr.pyHandle);
                        using (var obj = new PyObject(attr.pyHandle))
                        {
                            mod_dict.SetItem(s, obj);
                        }
                    }
                }
            }
            Runtime.XIncref(py_clr_module);
            return NewReference.DangerousFromPointer(py_clr_module);
        }

        /// <summary>
        /// The hook to import a CLR module into Python. Returns a new reference
        /// to the module.
        /// </summary>
        public static ModuleObject Import(string modname)
        {
            // Traverse the qualified module name to get the named module. 
            // Note that if
            // we are running in interactive mode we pre-load the names in
            // each module, which is often useful for introspection. If we
            // are not interactive, we stick to just-in-time creation of
            // objects at lookup time, which is much more efficient.
            // NEW: The clr got a new module variable preload. You can
            // enable preloading in a non-interactive python processing by
            // setting clr.preload = True

            ModuleObject head = null;
            ModuleObject tail = root;
            root.InitializePreload();

            string[] names = modname.Split('.');
            foreach (string name in names)
            {
                ManagedType mt = tail.GetAttribute(name, true);
                if (!(mt is ModuleObject))
                {
                    Exceptions.SetError(Exceptions.ImportError, $"'{name}' Is not a ModuleObject.");
                    throw new PythonException();
                }
                if (head == null)
                {
                    head = (ModuleObject)mt;
                }
                tail = (ModuleObject)mt;
                if (CLRModule.preload)
                {
                    tail.LoadNames();
                }
            }
            Console.WriteLine($"imported: {modname}");
            Console.Out.Flush();
            tail.IncrRefCount();
            return tail;
        }

        /// <summary>
        /// The actual import hook that ties Python to the managed world.
        /// </summary>
        public static IntPtr __import__(IntPtr self, IntPtr argsRaw, IntPtr kw)
        {
            var args = new BorrowedReference(argsRaw);

            // Replacement for the builtin __import__. The original import
            // hook is saved as this.py_import. This version handles CLR
            // import and defers to the normal builtin for everything else.

            var num_args = Runtime.PyTuple_Size(args);
            if (num_args < 1)
            {
                return Exceptions.RaiseTypeError("__import__() takes at least 1 argument (0 given)");
            }

            BorrowedReference py_mod_name = Runtime.PyTuple_GetItem(args, 0);
            if (py_mod_name.IsNull ||
                !Runtime.IsStringType(py_mod_name))
            {
                return Exceptions.RaiseTypeError("string expected");
            }

            // Check whether the import is of the form 'from x import y'.
            // This determines whether we return the head or tail module.

            BorrowedReference fromList = default;
            var fromlist = false;
            if (num_args >= 4)
            {
                fromList = Runtime.PyTuple_GetItem(args, 3);
                if (fromList != null &&
                    Runtime.PyObject_IsTrue(fromList) == 1)
                {
                    fromlist = true;
                }
            }

            string mod_name = Runtime.GetManagedString(py_mod_name);
            // Check these BEFORE the built-in import runs; may as well
            // do the Incref()ed return here, since we've already found
            // the module.
            if (mod_name == "clr")
            {
                NewReference clr_module = GetCLRModule(fromList);
                if (!clr_module.IsNull())
                {
                    BorrowedReference sys_modules = Runtime.PyImport_GetModuleDict();
                    if (!sys_modules.IsNull)
                    {
                        Runtime.PyDict_SetItemString(sys_modules, "clr", clr_module);
                    }
                }
                return clr_module.DangerousMoveToPointerOrNull();
            }

            string realname = mod_name;

            // 2010-08-15: Always seemed smart to let python try first...
            // This shaves off a few tenths of a second on test_module.py
            // and works around a quirk where 'sys' is found by the
            // LoadImplicit() deprecation logic.
            // Turns out that the AssemblyManager.ResolveHandler() checks to see if any
            // Assembly's FullName.ToLower().StartsWith(name.ToLower()), which makes very
            // little sense to me.
            IntPtr res = Runtime.PyObject_Call(py_import, args.DangerousGetAddress(), kw);
            if (res != IntPtr.Zero)
            {
                // There was no error.
                if (fromlist && IsLoadAll(fromList))
                {
                    var mod = ManagedType.GetManagedObject(res) as ModuleObject;
                    mod?.LoadNames();
                }
                return res;
            }
            // There was an error
            if (!Exceptions.ExceptionMatches(Exceptions.ImportError))
            {
                // and it was NOT an ImportError; bail out here.
                return IntPtr.Zero;
            }

            if (mod_name == string.Empty)
            {
                // Most likely a missing relative import.
                // For example site-packages\bs4\builder\__init__.py uses it to check if a package exists:
                //     from . import _html5lib
                // We don't support them anyway
                return IntPtr.Zero;
            }
            // Save the exception
            var originalException = new PythonException();
            // Otherwise,  just clear the it.
            Exceptions.Clear();

            string[] names = realname.Split('.');

            // See if sys.modules for this interpreter already has the
            // requested module. If so, just return the existing module.
            BorrowedReference modules = Runtime.PyImport_GetModuleDict();
            BorrowedReference module = Runtime.PyDict_GetItem(modules, py_mod_name);

            if (module != null)
            {
                if (fromlist)
                {
                    if (IsLoadAll(fromList))
                    {
                        var mod = ManagedType.GetManagedObject(module) as ModuleObject;
                        mod?.LoadNames();
                    }
                    return new NewReference(module).DangerousMoveToPointer();
                }

                module = Runtime.PyDict_GetItemString(modules, names[0]);
                return new NewReference(module, canBeNull: true).DangerousMoveToPointer();
            }
            Exceptions.Clear();

            // Traverse the qualified module name to get the named module
            // and place references in sys.modules as we go. Note that if
            // we are running in interactive mode we pre-load the names in
            // each module, which is often useful for introspection. If we
            // are not interactive, we stick to just-in-time creation of
            // objects at lookup time, which is much more efficient.
            // NEW: The clr got a new module variable preload. You can
            // enable preloading in a non-interactive python processing by
            // setting clr.preload = True

            ModuleObject head = mod_name == realname ? null : root;
            ModuleObject tail = root;
            root.InitializePreload();

            foreach (string name in names)
            {
                ManagedType mt = tail.GetAttribute(name, true);
                if (!(mt is ModuleObject))
                {
                    originalException.Restore();
                    return IntPtr.Zero;
                }
                if (head == null)
                {
                    head = (ModuleObject)mt;
                }
                tail = (ModuleObject)mt;
                if (CLRModule.preload)
                {
                    tail.LoadNames();
                }

                // Add the module to sys.modules
                Runtime.PyDict_SetItemString(modules, tail.moduleName, tail.ObjectReference);
            }

            {
                var mod = fromlist ? tail : head;

                if (fromlist && IsLoadAll(fromList))
                {
                    mod.LoadNames();
                }

                Runtime.XIncref(mod.pyHandle);
                return mod.pyHandle;
            }
        }

        private static bool IsLoadAll(BorrowedReference fromList)
        {
            if (fromList == null) throw new ArgumentNullException(nameof(fromList));

            if (CLRModule.preload)
            {
                return false;
            }
            if (Runtime.PySequence_Size(fromList) != 1)
            {
                return false;
            }
            using var fp = Runtime.PySequence_GetItem(fromList, 0);
            return Runtime.GetManagedString(fp) == "*";
        }
    }
}
