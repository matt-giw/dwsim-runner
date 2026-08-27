CORELIB = Flowsheet.GetType().GetType().Assembly
BFT = CORELIB.GetType("System.Reflection.BindingFlags")
def bf(n):
    return BFT.GetField(n).GetValue(None)
SFLAGS = bf("InvokeMethod") | bf("Static") | bf("Public")
PFLAGS = bf("GetProperty") | bf("Static") | bf("Public")
FileT = CORELIB.GetType("System.IO.File")
AppDomainT = CORELIB.GetType("System.AppDomain")
OUT = "/Users/mateuszgrobelny/dwsim_cases/enum_values.tsv"
L = []
domain = AppDomainT.InvokeMember("CurrentDomain", PFLAGS, None, None, ())
asms = list(domain.GetAssemblies())
seen = {}
for a in asms:
    try:
        types = list(a.GetTypes())
    except Exception:
        continue
    for t in types:
        try:
            if not t.IsEnum:
                continue
            fn = t.FullName
            if fn in seen:
                continue
            if ("UnitOperations" not in fn) and ("Interfaces" not in fn):
                continue
            seen[fn] = 1
            names = list(t.GetEnumNames())
            vals = list(t.GetEnumValues())
            for i in range(len(names)):
                try:
                    iv = int(vals[i])
                except Exception:
                    iv = -999
                L.append("EV\t%s\t%s\t%d" % (fn, names[i], iv))
        except Exception:
            pass
FileT.InvokeMember("WriteAllText", SFLAGS, None, None, (OUT, "\n".join(L)))
Flowsheet.WriteMessage("ENUMDUMP %d lines" % len(L))
