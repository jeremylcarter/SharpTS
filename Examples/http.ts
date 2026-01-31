// HTTP Server Example - A simple web server
// Usage: sharpts examples/http.ts
// Then visit http://localhost:3000/ in your browser

import http from "http";

const PORT = 3000;

const server = http.createServer((req, res) => {
  console.log("Request received: " + req.method + " " + req.url);

  // Set JSON content type
  res.setHeader("Content-Type", "application/json");
  res.writeHead(200);

  // Create response object
  const responseData = {
    message: "Hello from SharpTS HTTP server!",
    method: req.method,
    url: req.url,
  };

  res.end(JSON.stringify(responseData));
});

server.listen(PORT, () => {
  console.log("Server running at http://localhost:" + PORT + "/");
});
