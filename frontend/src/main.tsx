import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { createAppRouter } from "./routes";
import "./index.css";

const container = document.getElementById("root");
if (container === null) {
  throw new Error("The page has no #root element.");
}

createRoot(container).render(
  <StrictMode>
    <RouterProvider router={createAppRouter()} />
  </StrictMode>,
);
