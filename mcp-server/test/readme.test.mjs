import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const repoRoot = new URL("../../", import.meta.url);

test("README advertised tool count matches registered MCP tools", async () => {
  const [readme, indexSource] = await Promise.all([
    readFile(new URL("README.md", repoRoot), "utf8"),
    readFile(new URL("mcp-server/src/index.ts", repoRoot), "utf8"),
  ]);

  const registeredToolCount = [...indexSource.matchAll(/server\.registerTool\(/g)].length;
  assert.match(readme, new RegExp(`through ${registeredToolCount} local tools`));
});
