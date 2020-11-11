import subprocess
import os

def _run_test(testname):
    dirname = os.path.split(__file__)[0]
    exename = os.path.join(dirname, 'bin', 'Python.DomainReloadTests.exe'),
    proc = subprocess.Popen([
        exename,
        testname,
    ])
    proc.wait()

    assert proc.returncode == 0

def test_rename_class():
    _run_test('class_rename')

def test_rename_class_member_static_function():
    _run_test('static_member_rename')

def test_rename_class_member_function():
    _run_test('member_rename')

def test_rename_class_member_field():
    _run_test('field_rename')

def test_rename_class_member_property():
    _run_test('property_rename')