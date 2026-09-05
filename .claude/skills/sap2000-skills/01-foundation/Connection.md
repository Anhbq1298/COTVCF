# SAP2000 Connection & Initialization

**Category:** Foundation  
**Priority:** CRITICAL (read first)  
**Last Updated:** 2026-09-05

---

## Purpose

Establish reliable connection to SAP2000, obtain the `cSapModel` object, and manage application lifecycle.

This is the foundational skill—all other SAP2000 operations depend on correct connection handling.

---

## API Reference

### Primary Objects
- `SAPAPI.cOAPI` — COM entry point to SAP2000
- `cOAPI.AttachToInstance()` — Connect to running instance
- `cOAPI.CreateObject()` — Create new SAP2000 instance
- `cSapModel` — Main model object (all operations go through here)

### Key Methods
| Method | Returns | Purpose |
|--------|---------|---------|
| `cOAPI.AttachToInstance(iInstanceNumber)` | `int` (0=success) | Connect to existing SAP2000 instance |
| `cOAPI.CreateObject("SAP2000.SapObject")` | `cOAPI` | Create new SAP2000 application object |
| `cSapModel.SetPresentUnits(uunit)` | `int` | Set display/output units |
| `cSapModel.GetPresentUnits(ref uunit)` | `int` | Query current units |

---

## Project Conventions

### Connection Strategy
**Recommendation:** Attach to existing instance rather than create new.

```
Priority 1: Attach to running SAP2000
Priority 2: If fails, create new instance
Priority 3: If fails, throw exception (do not retry silently)
```

### Model Locking
SAP2000 locks the model during certain operations.

**Project Rule:**
- Check lock status before write operations
- Use `cSapModel.GetModelState()` to verify
- Throw clear exception if locked (do not wait indefinitely)

### Instance Management
**Project Rule:**
- One application, one `cOAPI` instance per connection
- Do not create multiple application instances (expensive, may conflict)
- Use dependency injection or singleton pattern for `cOAPI`

### Error Handling
**Project Rule:**
- All CSI methods return `int` status code
- Status 0 = success, nonzero = failure
- Do NOT ignore return codes (document why if ignored)
- Include context in exception: operation, status code, related object

---

## Workflow

### Step 1: Obtain cOAPI

```csharp
// Option A: Attach to existing instance
SAPAPI.cOAPI oapi = null;
int ret = AttachToSAP2000(out oapi);
if (ret != 0)
{
    throw new Exception($"Failed to attach to SAP2000 (code: {ret})");
}
```

### Step 2: Get cSapModel

```csharp
SAPAPI.cSapModel model = oapi.SapModel;
if (model == null)
{
    throw new Exception("Failed to obtain cSapModel from cOAPI");
}
```

### Step 3: Verify Connection

```csharp
int units = 0;
int ret = model.GetPresentUnits(ref units);
if (ret != 0)
{
    throw new Exception($"Failed to verify SAP2000 connection (code: {ret})");
}
```

### Step 4: Set Consistent Units

```csharp
// Set to mm, kN (project standard)
int ret = model.SetPresentUnits((int)eUnits.N_mm_C); // Newtons, mm, Celsius
if (ret != 0)
{
    throw new Exception($"Failed to set units to N_mm_C (code: {ret})");
}
```

---

## Code Example

### Managed Connection Service (Recommended)

```csharp
using SAPAPI;

public class SAPConnectionService : IDisposable
{
    private cOAPI _oapi;
    private cSapModel _model;
    private bool _isConnected;

    /// <summary>
    /// Attach to running SAP2000 instance or create new.
    /// </summary>
    public bool Connect()
    {
        try
        {
            // Step 1: Try to attach to existing instance
            _oapi = new cOAPI();
            int ret = _oapi.AttachToInstance(0); // Instance 0 = first running instance

            if (ret != 0)
            {
                // Step 2: Create new instance if attach fails
                _oapi = new cOAPI();
                object oapp = _oapi.CreateObject("SAP2000.SapObject");
                if (oapp == null)
                {
                    throw new Exception("Failed to create SAP2000 application object");
                }
            }

            // Step 3: Obtain model
            _model = _oapi.SapModel;
            if (_model == null)
            {
                throw new Exception("Failed to obtain cSapModel from cOAPI");
            }

            // Step 4: Verify connection
            int units = 0;
            ret = _model.GetPresentUnits(ref units);
            if (ret != 0)
            {
                throw new Exception($"SAP2000 connection verification failed (code: {ret})");
            }

            // Step 5: Set standard units (mm, kN)
            ret = _model.SetPresentUnits((int)eUnits.N_mm_C);
            if (ret != 0)
            {
                throw new Exception($"Failed to set units to N_mm_C (code: {ret})");
            }

            _isConnected = true;
            return true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            throw new InvalidOperationException("SAP2000 connection failed", ex);
        }
    }

    /// <summary>
    /// Get the SAP model object.
    /// </summary>
    public cSapModel Model
    {
        get
        {
            if (!_isConnected || _model == null)
            {
                throw new InvalidOperationException("Not connected to SAP2000. Call Connect() first.");
            }
            return _model;
        }
    }

    /// <summary>
    /// Disconnect from SAP2000.
    /// </summary>
    public void Disconnect()
    {
        if (_isConnected)
        {
            try
            {
                // Optionally close model without saving
                // _model.File.CloseFile(false);
            }
            catch { }

            _oapi = null;
            _model = null;
            _isConnected = false;
        }
    }

    public void Dispose()
    {
        Disconnect();
    }

    public bool IsConnected => _isConnected;
}
```

### Usage

```csharp
var service = new SAPConnectionService();
service.Connect();

try
{
    var model = service.Model;
    // Use model...
}
finally
{
    service.Disconnect();
}
```

---

## Common Pitfalls

### ❌ Ignoring Return Codes
```csharp
// WRONG: Return code ignored
_oapi.AttachToInstance(0);
cSapModel model = _oapi.SapModel; // May be null!
```

**Fix:** Always check return code and handle failure.

### ❌ Not Checking Model Lock
```csharp
// WRONG: May fail if model is locked
model.PointObj.AddPoint(...);
```

**Fix:** Check lock status before operations.

```csharp
int locked = 0;
model.GetModelState(ref locked);
if (locked != 0)
{
    throw new InvalidOperationException("Model is locked");
}
```

### ❌ Assuming Units
```csharp
// WRONG: Units may not be N_mm_C
double force = 50.0; // Is this 50 N or 50 kN?
```

**Fix:** Always set and verify units at connection time.

### ❌ Multiple cOAPI Instances
```csharp
// WRONG: Creates multiple instances
for (int i = 0; i < 5; i++)
{
    cOAPI oapi = new cOAPI();
    oapi.CreateObject(...);
}
```

**Fix:** Reuse single cOAPI instance or use dependency injection.

---

## Validation Checklist

✓ Connection established to SAP2000  
✓ `cSapModel` obtained successfully  
✓ Units set to N_mm_C (project standard)  
✓ All CSI return codes checked  
✓ Connection verified with `GetPresentUnits()`  
✓ Exception handling includes CSI return code and operation name  
✓ Model lock status checked before write operations  
✓ No multiple cOAPI instances created  
✓ cOAPI disposed properly (if using disposal pattern)  

---

## External References

- **CSI OAPI Help:** Search for `cOAPI`, `cSapModel` in SAP2000 Help
- **CSI GitHub Examples:** https://github.com/csi-ers/SAP2000-csharp-examples
- **SAP2000 Forum:** https://wiki.csiamerica.com/

---

## Related Skills

- [Units & Coordinates](UnitsCoordinates.md) — Set proper units after connection
- [Error Handling & Return Codes](ErrorHandling.md) — Interpret CSI return codes
- [Object Naming & Identification](ObjectNaming.md) — Name objects after creation
