import subprocess
import os

def runit(m1, m2, member):
    proc = subprocess.Popen([os.path.join(os.path.split(__file__)[0], 'bin', 'Python.DomainReloadTests.exe'), m1, m2, member])
    proc.wait()

    assert proc.returncode == 0

def test_rename_class():

    m1 = 'TestClass'
    m2 = 'TestClass2'
    member = ''
    runit(m1, m2, member)

