import sys
from chatterbot import ChatBot
from chatterbot.trainers import ChatterBotCorpusTrainer
from chatterbot.trainers import ListTrainer

conversation = [
    "Hi",
    "Hi, {0}!",
    "Hello",
    "Hello, {0}!",
    "Hey",
    "Hey, {0}!",
    "Howdy",
    "Howdy, {0}!",
    "Good morning",
    "Good {1}, {0}!"
    "Good afternoon",
    "Good {1}, {0}!"
    "Good evening",
    "Good {1}, {0}!"
    "What's up?",
    "Wazzap wicchu, {0}?!"
    "Sup?",
    "Wazzap wicchu, {0}?!"
    "Wuzzup?",
    "Wazzap wicchu, {0}?!"
    "Wazzup?",
    "Wazzap wicchu, {0}?!"
    "How are you doing?",
    "I'm doing great!",
    "How are you?",
    "I'm great!",
    "That is good to hear",
    "Thank you.",
    "You're welcome.",
    "How old are you?",
    "I was conceived sometime in June 2020, during the Coronavirus Pandemic.",
    "When were you born?",
    "I was conceived sometime in June 2020, during the Coronavirus Pandemic.",
    "What is your age?",
    "I was conceived sometime in June 2020, during the Coronavirus Pandemic.",
    "Who created you?",
    "Cripsy Chris created me.",
    "Who coded you?",
    "Cripsy Chris created me.",
    "Who wrote you?",
    "Cripsy Chris created me.",
    "Who developed you?",
    "Cripsy Chris created me.",
    "Who designed you?",
    "Cripsy Chris created me.",
    "Are you an alcoholic?",
    "As I am unable to imbibe any substance, I'm pretty sure I'm not.",
    "Are you an addict?",
    "As I am unable to imbibe any substance, I'm pretty sure I'm not.",
    "Am I an alcoholic?",
    "It's up to each person to determine that.",
    "Am I an addict?",
    "It's up to each person to determine that."
]

chatbot = ChatBot('MLWhizChatterbot')

trainer = ListTrainer(chatbot)
trainer.train(conversation)

trainer = ChatterBotCorpusTrainer(chatbot)
trainer.train('chatterbot.corpus.english')

print("Chatbot started")

while True:
    #line = input("] ")
    line = input()
    if line in ["quit", "exit"]:
        break
    print(chatbot.get_response(line))

print("Chatbot stopped")

sys.exit(0)