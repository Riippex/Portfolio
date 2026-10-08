import { describe, expect, it } from "vitest";
import { parseStage } from "./stage";

describe("parseStage", () => {
  it.each(["local", "dev", "prod"])("accepts %s", (value) => {
    expect(parseStage(value)).toBe(value);
  });

  it.each([undefined, null, "", " dev", "dev ", "DEV", "Prod", "development", "production", "staging", "test", 1, {}])(
    "rejects %j without defaulting",
    (value) => {
      expect(parseStage(value)).toBeNull();
    }
  );
});
