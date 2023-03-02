const button1 = document.createElement("button");
button1.innerHTML = "AC";
button1.classList.add("my-button-class");
button1.style.bottom = "84px";
button1.title = "Append clipboard";

document.body.appendChild(button1);

const button2 = document.createElement("button");
button2.innerHTML = "CP";
button2.classList.add("my-button-class");
button2.style.bottom = "24px";
button2.title = "Copy all answers to clipboard";

document.body.appendChild(button2);

function setTextToClipboard(text) {
  navigator.clipboard.readText().then((clipText) => {
    const newText = text + clipText;
    navigator.clipboard.writeText(newText);
  });
}

button1.addEventListener("click", () => {
  setTextToClipboard("Rewrite this in more simple words:\n\n");
});

button2.addEventListener("click", () => {
  const elements = document.querySelectorAll(
    ".markdown.prose.w-full.break-words.dark\\:prose-invert.dark"
  );
  let text = "";
  for (let i = 0; i < elements.length; i++) {
    const children = elements[i].querySelectorAll("*");
    for (let j = 0; j < children.length; j++) {
      text += children[j].textContent.trim() + "\n\n";
    }
  }
  navigator.clipboard.readText().then((clipText) => {
    navigator.clipboard.writeText(text);
  });
});
