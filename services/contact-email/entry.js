import { EmailMessage } from "cloudflare:email";
import { relay } from "./relay.mjs";

export default {
  fetch(request, env) {
    return relay(request, env, (from, to, raw) => new EmailMessage(from, to, raw));
  },
};
