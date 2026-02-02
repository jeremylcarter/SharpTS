// HTTP Server Example - A simple web server
// Usage: sharpts examples/http.ts
// Then visit http://localhost:3000/ in your browser

import http from "http";
import { formatRequestLog, greet } from "./test";

const PORT = 8080;

const server = http.createServer((req, res) => {
  console.log(formatRequestLog(req.method, req.url));

  // Set JSON content type
  res.setHeader("Content-Type", "application/json");
  res.writeHead(200);

  // Create response object
  const responseData = {
    message: greet("SharpTS"),
    method: req.method,
    url: req.url,
  };

  res.end(JSON.stringify(responseData));
});

server.listen(PORT, () => {
  console.log("Server running at http://localhost:" + PORT + "/");
});
