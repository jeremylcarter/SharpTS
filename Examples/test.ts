// test.ts - A TypeScript module to test module imports

/**
 * Formats a request log message
 */
export function formatRequestLog(method: string, url: string): string {
  const timestamp = new Date().toISOString();
  return `[${timestamp}] ${method} ${url}`;
}

/**
 * Creates a greeting message
 */
export function greet(name: string): string {
  return `Hello, ${name}!`;
}

// Default export
export default {
  version: "1.0.0",
  name: "test-module",
};
