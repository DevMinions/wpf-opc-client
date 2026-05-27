# Third Party Notices

Dc OPC Collector uses the following open-source packages. Each package's license
and copyright is preserved below. Full license texts are available at each
package's NuGet page or upstream repository.

This project as a whole is distributed under **GPL-3.0** (see [LICENSE](LICENSE))
because two of its runtime dependencies — `OPCFoundation.NetStandard.Opc.Ua.Client`
and `Technosoftware.DaAeHdaClient` — are GPL-licensed and trigger copyleft
propagation. Commercial licenses for those two libraries are available from their
respective vendors and would permit re-licensing this project under a permissive
license; while this project remains under GPL-3.0 in its open-source form.

---

## GPL-licensed dependencies (drive overall project license)

### OPCFoundation.NetStandard.Opc.Ua.Client

- **License:** GPL-2.0 (default for non-members of OPC Foundation)
- **Copyright:** OPC Foundation
- **Source:** <https://github.com/OPCFoundation/UA-.NETStandard>
- **Commercial license:** Available to OPC Foundation members (<https://opcfoundation.org/>).
- **Used in:** `Dc.Opc.Ua` (subscriber + browser).

### Technosoftware.DaAeHdaClient (Planned for Phase 3b)

- **License:** GPL-3.0 (community edition)
- **Copyright:** Technosoftware GmbH
- **Source:** <https://github.com/technosoftware-gmbh>
- **Commercial license:** Available from Technosoftware GmbH (<https://technosoftware.com/>).
- **Used in:** `Dc.Opc.Da` and `Dc.Opc.Ae` (when implemented on Windows side).

---

## Permissive (MIT / Apache-2.0) dependencies

### MIT License

Each of the following is © its respective author(s) and licensed under the MIT License.

| Package | Project / Repo |
|---|---|
| `CommunityToolkit.Mvvm` | Microsoft — <https://github.com/CommunityToolkit/dotnet> |
| `MessagePack` | neuecc / Cysharp — <https://github.com/MessagePack-CSharp/MessagePack-CSharp> |
| `Microsoft.EntityFrameworkCore` | Microsoft (.NET Foundation) — <https://github.com/dotnet/efcore> |
| `Microsoft.EntityFrameworkCore.Sqlite` | Microsoft (.NET Foundation) | as above |
| `Microsoft.EntityFrameworkCore.Design` | Microsoft (.NET Foundation) | as above |
| `Microsoft.Extensions.Hosting` | Microsoft (.NET Foundation) — <https://github.com/dotnet/runtime> |
| `Microsoft.Extensions.DependencyInjection` | Microsoft (.NET Foundation) | as above |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | Microsoft (.NET Foundation) | as above |
| `Microsoft.Extensions.Logging.Abstractions` | Microsoft (.NET Foundation) | as above |
| `H.NotifyIcon.Wpf` | HavenDV — <https://github.com/HavenDV/H.NotifyIcon> |
| `ClosedXML` | ClosedXML team — <https://github.com/ClosedXML/ClosedXML> |
| `Ulid` | Cysharp — <https://github.com/Cysharp/Ulid> |
| `Microsoft.NET.Test.Sdk` (test) | Microsoft | <https://github.com/microsoft/vstest> |

MIT License text (canonical form):

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

Each of the following is licensed under Apache License 2.0.

| Package | Project / Repo |
|---|---|
| `Serilog` | Serilog Contributors — <https://github.com/serilog/serilog> |
| `Serilog.Extensions.Hosting` | Serilog Contributors | <https://github.com/serilog/serilog-extensions-hosting> |
| `Serilog.Sinks.File` | Serilog Contributors | <https://github.com/serilog/serilog-sinks-file> |
| `Serilog.Sinks.Console` | Serilog Contributors | <https://github.com/serilog/serilog-sinks-console> |
| `EFCore.NamingConventions` | Shay Rojansky — <https://github.com/efcore/EFCore.NamingConventions> |
| `xunit` (test) | xUnit.net Project — <https://github.com/xunit/xunit> |
| `xunit.runner.visualstudio` (test) | xUnit.net Project | as above |

Full Apache 2.0 license text: <https://www.apache.org/licenses/LICENSE-2.0>

---

## References (not source code)

`opcda/article.md` is an external technical article authored by a third party. It is
referenced for design guidance and is NOT redistributed with this software. Refer
to its original source for licensing.
