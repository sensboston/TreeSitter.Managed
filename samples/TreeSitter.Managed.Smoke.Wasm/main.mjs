// The loader of the browser-wasm smoke run. The wasm CI job copies this file into the publish
// directory of samples/TreeSitter.Managed.Smoke.Wasm and runs `node main.mjs` there, so that
// ./_framework/dotnet.js is the config the SDK generated for this app. The exit code of the app
// (Program.Main of samples/TreeSitter.Managed.Smoke) is forwarded to node.
import { dotnet } from './_framework/dotnet.js';

// dotnet.run() is the shortcut of .NET 8; create().run() is the explicit form of the same.
const finished = typeof dotnet.run === 'function' ? await dotnet.run() : await (await dotnet.create()).run();
const exitCode = typeof finished === 'number' ? finished : finished?.exitCode ?? 0;
if (exitCode !== 0) {
  process.exitCode = exitCode;
}
