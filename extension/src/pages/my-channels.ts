import type { MyChannelsReply } from "../shared/messages";
import { channelLabel } from "../shared/request-names";
import { YOUTUBE_ORIGIN, channelSearchUrl, youTubeSiteOf } from "../shared/youtube-url";
import { ask, byId, localizePage, onBroadcast, t } from "./common";

function channelUrl(channelId: string): string {
  return `https://www.youtube.com/channel/${encodeURIComponent(channelId)}`;
}

/**
 * The YouTube origin a search goes to: the one the bridge sent the child here from (my-channels.html#<origin>) when it is
 * youtube.com over https, else www.youtube.com. Any page may link here with a hash, so only a YouTube origin is taken.
 */
function searchOrigin(): string {
  try {
    const from = new URL(decodeURIComponent(location.hash.slice(1)));
    if (from.protocol === "https:" && youTubeSiteOf(from.hostname) === "www") return from.origin;
  } catch {
    // no hash, or not a URL
  }
  return YOUTUBE_ORIGIN;
}

function render(reply: MyChannelsReply | null): void {
  const list = byId<HTMLUListElement>("channels");
  list.replaceChildren();
  const channels = reply?.channels ?? [];
  byId("empty").hidden = channels.length > 0;
  for (const channel of channels) {
    const item = document.createElement("li");
    const link = document.createElement("a");
    link.href = channelUrl(channel.channelId);
    link.target = "_top";
    link.rel = "noopener";
    const title = document.createElement("span");
    title.className = "channel-title";
    // The channel's title, else its handle, else its id (an approval stores the names since PROTECTION-SPEC §15).
    title.textContent = channel.title || channel.handle || channel.channelId;
    title.dir = "auto";
    link.append(title);
    if (channel.handle && channel.title) {
      const handle = document.createElement("span");
      handle.className = "channel-handle";
      handle.dir = "ltr";
      handle.textContent = channel.handle;
      link.append(handle);
    }
    link.title = channelLabel(channel.title, channel.handle) ?? channel.channelId;
    item.append(link);
    list.append(item);
  }
  // Channel search (PROTECTION-SPEC §15): a search box, and an introduction that says a found channel needs approval.
  const search = reply?.channelSearch === true;
  byId("search").hidden = !search;
  byId("intro").textContent = t(search ? "myChannelsIntroSearch" : "myChannelsIntro");
  const root = document.documentElement;
  root.dataset.channels = String(channels.length);
  root.dataset.channelSearch = String(search);
}

/** A search from the box opens YouTube's results restricted to channels, in this tab. */
function wireSearch(): void {
  const form = byId<HTMLFormElement>("search");
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const query = byId<HTMLInputElement>("query").value.trim();
    if (!query || form.hidden) return;
    location.assign(channelSearchUrl(query, searchOrigin()));
  });
}

async function main(): Promise<void> {
  localizePage();
  document.documentElement.dataset.page = "my-channels";
  wireSearch();
  render(await ask<MyChannelsReply>({ type: "wf:getMyChannels" }));
  document.documentElement.dataset.ready = "true";
  onBroadcast(async (message) => {
    if (message.type === "wf:stateChanged") render(await ask<MyChannelsReply>({ type: "wf:getMyChannels" }));
  });
}

void main();
