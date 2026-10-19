import type { MyChannelsReply } from "../shared/messages";
import { ask, byId, localizePage, onBroadcast } from "./common";

function channelUrl(channelId: string): string {
  return `https://www.youtube.com/channel/${encodeURIComponent(channelId)}`;
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
    title.textContent = channel.title || channel.handle || channel.channelId;
    link.append(title);
    if (channel.handle && channel.title) {
      const handle = document.createElement("span");
      handle.className = "channel-handle";
      handle.dir = "ltr";
      handle.textContent = channel.handle;
      link.append(handle);
    }
    item.append(link);
    list.append(item);
  }
  document.documentElement.dataset.channels = String(channels.length);
}

async function main(): Promise<void> {
  localizePage();
  document.documentElement.dataset.page = "my-channels";
  render(await ask<MyChannelsReply>({ type: "wf:getMyChannels" }));
  document.documentElement.dataset.ready = "true";
  onBroadcast(async (message) => {
    if (message.type === "wf:stateChanged") render(await ask<MyChannelsReply>({ type: "wf:getMyChannels" }));
  });
}

void main();
