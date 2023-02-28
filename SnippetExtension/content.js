const button1 = document.createElement("button");
button1.innerHTML = "S";
button1.classList.add("my-button-class");
button1.style.bottom = "204px";

document.body.appendChild(button1);

const button2 = document.createElement("button");
button2.innerHTML = "A1";
button2.classList.add("my-button-class");
button2.style.bottom = "144px";

document.body.appendChild(button2);

const button3 = document.createElement("button");
button3.innerHTML = "A2";
button3.classList.add("my-button-class");
button3.style.bottom = "84px";

document.body.appendChild(button3);

const button4 = document.createElement("button");
button4.innerHTML = "B1";
button4.classList.add("my-button-class");
button4.style.bottom = "24px";

document.body.appendChild(button4);

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
  setTextToClipboard("Rewrite this and use A1 English vocabulary:\n\n");
});

button3.addEventListener("click", () => {
  setTextToClipboard("Rewrite this and use A2 English vocabulary:\n\n");
});

button4.addEventListener("click", () => {
  setTextToClipboard("Rewrite this and use B1 English vocabulary:\n\n");
});
