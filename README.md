# unity-rwx

**unity-rwx** is a Unity-based RWX (RenderWare Script) loader and runtime execution system inspired by **ActiveWorlds / Virtual Paradise** object behavior.  
It focuses on correctness, performance, and extensibility while supporting VP-style actions such as textures, normal maps, ambient lighting, scale, shear, and future animation support.

This project is designed for large streamed worlds where thousands of mostly-static RWX objects must load quickly and behave consistently.

---

## ✨ Features

- ✅ RWX geometry parsing (walls, tris, prims)
- ✅ VP-style action execution
  - `texture`
  - `normalmap`
  - `ambient`
  - `scale`
  - `shear` (world-space correct)
- ✅ ZIP-based model loading with caching
- ✅ Shared-material workflow (no accidental instancing)
- ✅ DDS / PNG / JPG texture support
- ✅ Texture and normal-map caching
- ✅ Unity Built-in Render Pipeline compatible
- 🚧 Animation hooks (planned)
- 🚧 Object action streaming (planned)
