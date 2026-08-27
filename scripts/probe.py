# DWSIM unit-op parameter probe -- reflection only (no imports; file layer is broken)
CORELIB = Flowsheet.GetType().GetType().Assembly
BFT = CORELIB.GetType("System.Reflection.BindingFlags")
def bf(n):
    return BFT.GetField(n).GetValue(None)
SFLAGS = bf("InvokeMethod") | bf("Static") | bf("Public")
PFLAGS = bf("GetProperty") | bf("Static") | bf("Public")
FileT = CORELIB.GetType("System.IO.File")
EnumT = CORELIB.GetType("System.Enum")
AppDomainT = CORELIB.GetType("System.AppDomain")

OUT = "/Users/mateuszgrobelny/dwsim_cases/unitop_params.tsv"
L = []
def emit(s):
    L.append(s)

def write_out():
    FileT.InvokeMember("WriteAllText", SFLAGS, None, None, (OUT, "\n".join(L)))

domain = AppDomainT.InvokeMember("CurrentDomain", PFLAGS, None, None, ())
asms = list(domain.GetAssemblies())
emit("ASMCOUNT\t%d" % len(asms))

def find_type(fullname):
    for a in asms:
        try:
            t = a.GetType(fullname)
        except Exception:
            t = None
        if t is not None:
            return t
    return None

ObjTypeT = find_type("DWSIM.Interfaces.Enums.GraphicObjects.ObjectType")
PropTypeT = find_type("DWSIM.Interfaces.Enums.PropertyType")
emit("OBJTYPE_T\t%s" % ObjTypeT)
emit("PROPTYPE_T\t%s" % PropTypeT)

ptALL = None
if PropTypeT is not None:
    for cand in ("ALL", "All"):
        try:
            ptALL = PropTypeT.GetField(cand).GetValue(None)
            break
        except Exception:
            pass
emit("PT_ALL\t%s" % ptALL)

def parse_enum(t, name):
    return EnumT.InvokeMember("Parse", SFLAGS, None, None, (t, name))

def get_obj(tag):
    for fn in ("GetFlowsheetSimulationObject", "GetObject"):
        try:
            o = getattr(Flowsheet, fn)(tag)
            if o is not None:
                return o
        except Exception:
            pass
    return None

def kill(tag):
    for fn in ("DeleteObject", "DeleteSelectedObject"):
        try:
            getattr(Flowsheet, fn)(tag)
            return True
        except Exception:
            pass
    return False

names = list(ObjTypeT.GetEnumNames())
emit("ENUMCOUNT\t%d" % len(names))

for n in names:
    tag = "PROBE_%s" % n
    try:
        ot = parse_enum(ObjTypeT, n)
    except Exception as e:
        emit("TYPE\t%s\tPARSE_ERR\t%s" % (n, e))
        continue
    try:
        Flowsheet.AddObject(ot, 60, 60, tag)
    except Exception as e:
        emit("TYPE\t%s\tADD_ERR\t%s" % (n, str(e)[:200]))
        continue
    o = get_obj(tag)
    if o is None:
        emit("TYPE\t%s\tNO_HANDLE" % n)
        kill(tag)
        continue
    try:
        clr = o.GetType().FullName
    except Exception:
        clr = "?"
    props = []
    if ptALL is not None:
        try:
            props = list(o.GetProperties(ptALL))
        except Exception as e:
            emit("TYPE\t%s\tGETPROPS_ERR\t%s" % (n, str(e)[:200]))
    emit("TYPE\t%s\tOK\t%s\t%d" % (n, clr, len(props)))
    for p in props:
        u = ""
        v = ""
        try:
            u = str(o.GetPropertyUnit(p, "SI"))
        except Exception:
            try:
                u = str(o.GetPropertyUnit(p))
            except Exception:
                u = ""
        try:
            v = str(o.GetPropertyValue(p))
        except Exception:
            v = ""
        emit("PROP\t%s\t%s\t%s\t%s" % (n, p, u, v))
    # CLR-level public instance properties (catches calculation modes / enums)
    try:
        for pi in o.GetType().GetProperties():
            try:
                pt = pi.PropertyType
                emit("CLRPROP\t%s\t%s\t%s\t%s" % (n, pi.Name, pt.FullName, pi.CanWrite))
                if pt.IsEnum:
                    opts = ",".join([str(x) for x in pt.GetEnumNames()])
                    emit("ENUMOPT\t%s\t%s\t%s\t%s" % (n, pi.Name, pt.FullName, opts))
            except Exception:
                pass
    except Exception:
        pass
    kill(tag)

write_out()
try:
    Flowsheet.WriteMessage("PROBE DONE: %d lines -> %s" % (len(L), OUT))
except Exception:
    pass
