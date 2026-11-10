import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import App from "./App";
import { PassView } from "./Checkin";
import { readPassFromHash } from "./lib/checkin";
import "./index.css";

const pass = readPassFromHash();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {pass ? <PassView data={pass} /> : <App />}
  </StrictMode>,
);
