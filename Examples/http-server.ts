// HTTP Server Example - A simple web server with routing
// Usage: sharpts examples/http.ts
//
// Demonstrates: http (createServer, listen), url (parse)
//
// Test with:
//   curl http://localhost:3000/
//   curl http://localhost:3000/api/info
//   curl "http://localhost:3000/api/echo?message=hello"

import http from "http";
import url from "url";

const PORT = 3000;

const server = http.createServer((req, res) => {
  // Parse the URL
  const parsedUrl = url.parse(req.url);
  const pathname = parsedUrl.pathname;
  const query = parsedUrl.query;

  // Handle different routes
  if (pathname === "/") {
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end("Welcome to SharpTS HTTP Server");
  } else if (pathname === "/api/info") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ server: "SharpTS", version: "1.0.0" }));
  } else if (pathname === "/api/echo") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ path: pathname, url: req.url }));
  } else if (pathname === "/api/time") {
    res.writeHead(200, { "Content-Type": "application/json" });
    const now = Date.now();
    res.end(JSON.stringify({ timestamp: now }));
  } else {
    res.writeHead(404, { "Content-Type": "text/plain" });
    res.end("Not Found: " + pathname);
  }
});

server.listen(PORT, () => {
  console.log("Server running at http://localhost:" + PORT + "/");
  console.log("Press Ctrl+C to stop");
});
