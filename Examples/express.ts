import express from "express";
const app = express();
const port = 8080;
app.get("/", (_req, res) => {
  res.send("Hello World!");
});
const server = app.listen(port, "0.0.0.0", () => {
  console.log("LISTENING");
});
server.on("error", (err) => {
  console.error("SERVER ERROR:", err);
});
